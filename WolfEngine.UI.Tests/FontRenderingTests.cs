using Microsoft.Extensions.DependencyInjection;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Tooling.Fonts;
using WolfEngine.Importing;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Shaders;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.UI;

namespace WolfEngine.UI.Tests;

public sealed class FontRenderingTests
{
	private string _directory = null!;
	private byte[] _cooked = null!;
	private UiFontCatalog _catalog = null!;
	private UiTextService _text = null!;
	private static readonly ComputedStyle Style = ComputedStyle.Default with { FontFamily = "Inter", FontSize = 24, LineHeight = 1.2f };
	private sealed class Provider(byte[] cooked) : IFontContentProvider
	{
		public bool TryResolve(string source, out Guid id, out string revision)
		{ id = new Guid("609d52d7-f446-44af-88ac-2000ebdb9f43"); revision = "test"; return source == "Assets/Fonts/Inter.ttf"; }
		public Stream Open(Guid id) => new MemoryStream(cooked, writable: false);
	}

	[OneTimeSetUp]
	public void BakeFixture()
	{
		_directory = Path.Combine(Path.GetTempPath(), "WolfUiFonts-" + Guid.NewGuid().ToString("N"));
		var result = new FontCompiler().Compile(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures/Inter.ttf"), _directory, new FontImportSettings());
		_cooked = File.ReadAllBytes(result.ArtifactPath);
		_catalog = new UiFontCatalog(new Provider(_cooked));
		_text = new UiTextService(_catalog);
		_text.SetFonts(new Dictionary<string, string> { ["Inter"] = "Assets/Fonts/Inter.ttf" });
	}
	[OneTimeTearDown]
	public void Cleanup() { _catalog.Dispose(); Directory.Delete(_directory, recursive: true); }

	[Test]
	public void ShapingRemainsValidAfterCompactingCollections()
	{
		for (var i = 0; i < 5; i++)
		{
			GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
			var text = _text.Layout("Compacting GC " + i, Style);
			Assert.That(text.Glyphs, Has.Length.GreaterThan(0));
		}
	}

	[TestCase("Short\n\nA much longer line", "Center", 400)]
	[TestCase("Short\nA much longer line", "Right", 400)]
	[TestCase("A paragraph with several words wrapping onto different lines", "Center", 150)]
	[TestCase("A paragraph with several words wrapping onto different lines", "Right", 150)]
	public void EachLineIsAlignedWithinItsActualTextBox(string value, string alignmentName, float width)
	{
		var alignment = Enum.Parse<UiTextAlign>(alignmentName);
		var layout = _text.Layout(value, Style, width);
		Assert.That(layout.LineWidths.Length, Is.GreaterThan(1));
		var alignedStyle = Style with { TextAlign = alignment };
		Assert.That(_text.Layout(value, alignedStyle, width), Is.SameAs(layout), "Alignment must share measurement caches.");
		var node = new UiNode { Name = "#text", Text = value, Width = width, Height = layout.Height, Style = alignedStyle, TextLayout = layout };
		var frame = new UiFrameBuilder(_text).Build(node, 500, 400);
		try
		{
			for (var i = 0; i < layout.Glyphs.Length; i++)
			{
				var placement = layout.Glyphs[i];
				var lineWidth = layout.LineWidths[placement.Line];
				var expectedOffset = (width - lineWidth) * (alignment == UiTextAlign.Center ? .5f : 1);
				Assert.That(frame.Vertices[i * 4].Position.X,
					Is.EqualTo(placement.Origin.X + placement.Glyph.Left * Style.FontSize + expectedOffset).Within(.001));
			}
		}
		finally { frame.Release(); }
	}

	[Test]
	public void TextAlignmentInheritsAndUpdatesWithoutInvalidatingLayout()
	{
		UiNode Tree(string alignment)
		{
			var root = new UiNode { Name = "div" };
			root.Attributes["style"] = "text-align: " + alignment;
			var child = new UiNode { Name = "#text", Text = "Short\nLonger line" }; root.Children.Add(child);
			CssStyleSheet.Parse("").Apply(root, 400, 200);
			return root;
		}
		var retained = Tree("left"); var updated = Tree("center");
		Assert.That(updated.Children[0].Style.TextAlign, Is.EqualTo(UiTextAlign.Center));
		var changes = UiTreeReconciler.Reconcile(retained, updated);
		Assert.That(changes.LayoutChanged, Is.False);
		Assert.That(retained.Children[0].Style.TextAlign, Is.EqualTo(UiTextAlign.Center));
		var child = retained.Children[0]; child.Attributes["style"] = "text-align: right; text-align: inherit";
		CssStyleSheet.Parse("").Apply(retained, 400, 200);
		Assert.That(child.Style.TextAlign, Is.EqualTo(UiTextAlign.Center));
	}

	[TestCase(GraphicsBackendKind.Metal)]
	[TestCase(GraphicsBackendKind.D3D12)]
	public void FontShaderCompilesWithBackendReflection(GraphicsBackendKind backend)
	{
		if (backend == GraphicsBackendKind.Metal && !OperatingSystem.IsMacOS() ||
		    backend == GraphicsBackendKind.D3D12 && !OperatingSystem.IsWindows()) Assert.Ignore("Requires the host backend's native shader compiler.");
		var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
		while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "WolfEngine", "Shaders"))) directory = directory.Parent;
		Assert.That(directory, Is.Not.Null, "Run the shader integration test from the engine checkout.");
		var provider = new DevelopmentShaderProvider(
			new EngineShaderOptions { EngineContentRoot = Path.Combine(directory!.FullName, "WolfEngine") },
			new EngineShaderCatalog());
		var compiled = provider.GetGraphicsShaderWithReflection(EngineShaderPrograms.ImGui,
			"vertexShader", "fragmentShader", backend);
		var buffer = compiled.ReflectionLayout.GetConstantBuffer(backend == GraphicsBackendKind.Metal ? "ImGuiBindless" : "UiDraw");
		Assert.That(buffer.Fields.Keys, Does.Contain("sampleTexture"));
		Assert.That(buffer.Fields.Keys, Does.Contain("distanceRange"));
		Assert.That(compiled.Bytecode.Vertex!.Value.Length, Is.GreaterThan(0));
	}

	[Test]
	public void PackagedFontLoadsWithoutAnySourceAssetOrLibraryDirectory()
	{
		var id = new Guid("609d52d7-f446-44af-88ac-2000ebdb9f43");
		var pack = Path.Combine(_directory, "fonts.wolfpack");
		WolfPackFile.Write(pack, [new WolfPackSource(id, "font", _cooked, [])]);
		var manifest = new WolfBootstrapManifest
		{
			FontSources = new() { ["Assets/Fonts/Inter.ttf"] = id },
			Packs = [new WolfManifestPack { Name = "fonts", FileName = "fonts.wolfpack", ByteSize = new FileInfo(pack).Length,
				Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(pack))) }]
		};
		var path = Path.Combine(_directory, "bootstrap.json");
		File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(manifest));
		using var packCatalog = new WolfPackCatalog(path);
		using var fonts = new UiFontCatalog(new WolfPackFontContentProvider(packCatalog));
		var text = new UiTextService(fonts);
		text.SetFonts(new Dictionary<string, string> { ["Inter"] = "Assets/Fonts/Inter.ttf" });
		var layout = text.Layout("Packaged gameplay", Style);
		Assert.That(layout.Font, Is.Not.Null);
		Assert.That(layout.Glyphs, Has.Length.GreaterThan(0));
		Assert.That(layout.Width, Is.GreaterThan(0));
	}

	[Test]
	public void LargeTextKeeps32BitIndicesAndBatchesSolidsWithItsAtlas()
	{
		var root = new UiNode { Name = "root", Width = 200, Height = 100 };
		var layout = _text.Layout("WolfEngine", Style);
		for (var i = 0; i < 2000; i++)
		{
			var cell = new UiNode { Name = "div", Width = 100, Height = 30, Style = Style with { Background = new ColorRGBA(0, 0, 0, 1) } };
			cell.Children.Add(new UiNode { Name = "#text", Text = "WolfEngine", Width = 100, Height = 30, Style = Style, TextLayout = layout });
			root.Children.Add(cell);
		}
		var frame = new UiFrameBuilder(_text).Build(root, 200, 100);
		try
		{
			Assert.That(frame.VertexCount, Is.GreaterThan(65535));
			Assert.That(frame.Indices.Take(frame.IndexCount).Max(), Is.GreaterThan(65535));
			Assert.That(frame.CommandCount, Is.LessThanOrEqualTo(2));
		}
		finally { frame.Release(); }
	}

	[Test]
	public void ContainedTextMatchesFullYogaWhenAnUncontainedSiblingAlsoChanges()
	{
		UiNode Tree(string header, string counter)
		{
			var root = new UiNode { Name = "root" };
			root.Children.Add(new UiNode { Name = "#text", Text = header, Style = Style });
			var cell = new UiNode { Name = "div", Style = Style with { Width = UiLength.Pixels(100), Height = UiLength.Pixels(40), AlignItems = "center", JustifyContent = "center" } };
			cell.Children.Add(new UiNode { Name = "#text", Text = counter, Style = Style }); root.Children.Add(cell);
			return root;
		}
		var retained = Tree("iii", "iii");
		using var incremental = new YogaLayoutEngine(_text);
		incremental.Layout(retained, 300, 200);
		foreach (var header in new[] { "Wide header that wraps over several lines", "Short", "Another header" })
		{
			var updated = Tree(header, "WWW");
			var changes = UiTreeReconciler.Reconcile(retained, updated);
			incremental.Layout(retained, 300, 200, changes.LayoutChanged);
			using var full = new YogaLayoutEngine(_text);
			full.Layout(updated, 300, 200);
			var actual = retained.Children[1].Children[0]; var expected = updated.Children[1].Children[0];
			Assert.That(actual.Left, Is.EqualTo(expected.Left).Within(0.6));
			Assert.That(actual.Top, Is.EqualTo(expected.Top).Within(0.6));
			Assert.That(actual.Width, Is.EqualTo(expected.Width).Within(0.6));
			Assert.That(actual.TextLayout!.Width, Is.EqualTo(expected.TextLayout!.Width).Within(0.1));
		}
		incremental.Layout(retained, 400, 200);
		var resized = Tree("Another header", "WWW");
		using var resizedFull = new YogaLayoutEngine(_text); resizedFull.Layout(resized, 400, 200);
		Assert.That(retained.Children[1].Children[0].Left, Is.EqualTo(resized.Children[1].Children[0].Left).Within(0.6));
	}

	[Test]
	public void IncrementalTextRespectsAsymmetricRelativePadding()
	{
		UiNode Tree(string value)
		{
			var root = new UiNode { Name = "root" };
			var cell = new UiNode { Name = "div", Style = Style with { Width = UiLength.Pixels(200), Height = UiLength.Pixels(100),
				Padding = new UiEdges(new UiLength(.5f, UiLengthUnit.Em), UiLength.Pixels(20), UiLength.Pixels(30), new UiLength(2, UiLengthUnit.Rem)),
				AlignItems = "center", JustifyContent = "center" } };
			cell.Children.Add(new UiNode { Name = "#text", Text = value, Style = Style }); root.Children.Add(cell); return root;
		}
		var retained = Tree("iii");
		using var incremental = new YogaLayoutEngine(_text); incremental.Layout(retained, 400, 200);
		var updated = Tree("WWW"); var changes = UiTreeReconciler.Reconcile(retained, updated);
		incremental.Layout(retained, 400, 200, changes.LayoutChanged);
		using var full = new YogaLayoutEngine(_text); full.Layout(updated, 400, 200);
		Assert.That(retained.Children[0].ResolvedPadding, Is.EqualTo(new UiInsets(12, 20, 30, 32)));
		var actual = retained.Children[0].Children[0]; var expected = updated.Children[0].Children[0];
		Assert.That(actual.Left, Is.EqualTo(expected.Left).Within(.6)); Assert.That(actual.Top, Is.EqualTo(expected.Top).Within(.6));
		Assert.That(actual.TextLayout!.Width, Is.EqualTo(expected.TextLayout!.Width));
	}

	[Test]
	public void ContextualLatinGlyphsAreInTheCookedCoverage()
	{
		var layout = _text.Layout("SCREEN-SPACE office affinity ffi fl fi", Style);
		Assert.That(layout.Glyphs.Select(g => g.Glyph.Id), Does.Not.Contain(0u));
	}

	[Test]
	public void CssFontFaceResolvesSourceAndInheritsTypography()
	{
		var sheet = CssStyleSheet.Parse("""
			@font-face { font-family: "Inter"; src: url("/Assets/Fonts/Inter.ttf"); }
			.label { font-family: "Inter", sans-serif; font-size: 24px; line-height: 32px; white-space: nowrap; }
			""");
		var root = new UiNode { Name = "div" };
		root.Attributes["class"] = "label";
		var text = new UiNode { Name = "#text", Text = "Hello" }; root.Children.Add(text);
		sheet.Apply(root, 200, 100);
		Assert.That(sheet.FontSources["Inter"], Is.EqualTo("Assets/Fonts/Inter.ttf"));
		Assert.That(text.Style.FontFamily, Is.EqualTo(root.Style.FontFamily));
		Assert.That(_text.Layout("Hello", text.Style).Height, Is.EqualTo(32));
		Assert.That(text.Style.NoWrap, Is.True);
	}

	[Test]
	public void SameLengthChangeRemeasuresYogaAndOnlyInvalidatesText()
	{
		var root = new UiNode { Name = "root" };
		var text = new UiNode { Name = "#text", Text = "iii", Style = Style }; root.Children.Add(text);
		using var yoga = new YogaLayoutEngine(_text);
		yoga.Layout(root, 400, 100);
		var width = text.TextLayout!.Width;
		var updated = new UiNode { Name = "root" };
		updated.Children.Add(new UiNode { Name = "#text", Text = "WWW", Style = Style });
		var changes = UiTreeReconciler.Reconcile(root, updated);
		Assert.That(changes.LayoutChanged, Is.False);
		Assert.That(changes.IntrinsicSizeChanged, Is.True);
		yoga.Layout(root, 400, 100, fullLayoutRequired: false);
		Assert.That(text.TextLayout!.Width, Is.GreaterThan(width * 2));
	}

	[Test]
	public void WrappingAndRenderingUseTheSameCachedPositions()
	{
		var wrapped = _text.Layout("WolfEngine gameplay UI", Style, 120);
		var single = _text.Layout("WolfEngine gameplay UI", Style with { NoWrap = true }, 120);
		Assert.That(wrapped.Height, Is.GreaterThan(single.Height));
		Assert.That(wrapped.Width, Is.LessThanOrEqualTo(120));
		Assert.That(_text.Layout("WolfEngine gameplay UI", Style, 120), Is.SameAs(wrapped));
		var root = new UiNode { Name = "#text", Text = "WolfEngine gameplay UI", Style = Style, Width = 120, TextLayout = wrapped };
		var frame = new UiFrameBuilder(_text).Build(root, 200, 200);
		try
		{
			Assert.That(frame.VertexCount, Is.EqualTo(wrapped.Glyphs.Length * 4));
			Assert.That(frame.Commands[0].Atlas, Is.SameAs(wrapped.Font!.Atlas));
			Assert.That(frame.Commands[0].DistanceRange, Is.EqualTo(6));
		}
		finally { frame.Release(); }
	}

	[Test]
	public void AtlasIsSharedAcrossSurfacesAndWarmMeasurementDoesNotAllocate()
	{
		var second = new UiTextService(_catalog);
		second.SetFonts(new Dictionary<string, string> { ["Inter"] = "Assets/Fonts/Inter.ttf" });
		var run = _text.Layout("12345", Style);
		Assert.That(second.Layout("12345", Style).Font!.Atlas, Is.SameAs(run.Font!.Atlas));
		for (var i = 0; i < 100; i++) _text.Layout("12345", Style);
		var before = GC.GetAllocatedBytesForCurrentThread();
		for (var i = 0; i < 1000; i++) _text.Layout("12345", Style);
		var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.That(allocated, Is.Zero);
	}

	[Test]
	public void UnbakedGlyphUsesNotdefInsteadOfReadingSourceAtRuntime()
	{
		var run = _text.Layout("\u03a9", Style);
		Assert.That(run.Glyphs.Single().Glyph.Id, Is.Zero);
	}

	[TestCase("../Inter.ttf")]
	[TestCase("https://example.com/Inter.ttf")]
	[TestCase("/Assets/../Inter.ttf")]
	public void FontReferencesCannotEscapeTheProject(string path) => Assert.Throws<ArgumentException>(() => FontSourceReferences.Normalize(path));
}
