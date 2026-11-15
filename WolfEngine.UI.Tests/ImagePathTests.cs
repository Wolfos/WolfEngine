using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using WolfEngine.AssetPipeline;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.UI.Tests;

[NonParallelizable]
public sealed class ImagePathTests
{
	private sealed class Registry : IAssetInstanceRegistry
	{
		public Dictionary<Guid, object> Instances { get; } = [];

		public object? GetInstance(Guid assetId, Type expectedType)
		{
			return Instances.GetValueOrDefault(assetId);
		}

		public void RefreshProject(string projectRootPath, AssetDatabase database)
		{
		}

		public void InvalidateAssets(IEnumerable<Guid> assetIds)
		{
		}

		public void ClearCachedInstances()
		{
		}

		public void Clear()
		{
		}
	}

	private sealed class Paths : ITextureSourceProvider
	{
		public Dictionary<string, Guid> Sources { get; } = new(StringComparer.Ordinal);

		public bool TryResolve(string sourcePath, out Guid nodeId)
		{
			nodeId = Guid.Empty;

			return TextureSourceReferences.TryNormalize(sourcePath, out var path) &&
			       Sources.TryGetValue(path, out nodeId);
		}
	}

	private Registry _registry = null!;
	private Paths _paths = null!;

	[SetUp]
	public void SetUp()
	{
		_registry = new Registry();
		_paths = new Paths();
		AssetDatabase.SetInstanceRegistry(_registry);
	}

	[TearDown]
	public void TearDown()
	{
		AssetDatabase.ClearInstanceRegistry();
	}

	[Test]
	public void ProjectPathsNormaliseAndEverythingElseIsRejected()
	{
		Assert.Multiple(() =>
		{
			Assert.That(TextureSourceReferences.TryNormalize("/Assets/UI/Potion.png", out var rooted), Is.True);
			Assert.That(rooted, Is.EqualTo("Assets/UI/Potion.png"));
			Assert.That(TextureSourceReferences.TryNormalize("Assets\\UI\\Potion.png", out var windows), Is.True);
			Assert.That(windows, Is.EqualTo("Assets/UI/Potion.png"));
			Assert.That(TextureSourceReferences.TryNormalize("/Assets/../Secrets.png", out _), Is.False);
			Assert.That(TextureSourceReferences.TryNormalize("C:/Assets/UI/Potion.png", out _), Is.False);
			Assert.That(TextureSourceReferences.TryNormalize("/Textures/Potion.png", out _), Is.False);
			Assert.That(TextureSourceReferences.TryNormalize("/Assets//Potion.png", out _), Is.False);
		});
	}

	[Test]
	public void CssScanFindsBackgroundUrlsButNotFontsOrAssetReferences()
	{
		var css = """
			@font-face { font-family: "Inter"; src: url("/Assets/Fonts/Inter.ttf"); }
			.a { background-image: url('/Assets/UI/Panel.png'); }
			.b { background: #000 url(/Assets/UI/Tile.png) repeat; }
			/* .c { background-image: url(/Assets/UI/Commented.png); } */
			.d { background-image: url(asset:6f0e7f2e-0000-4000-8000-000000000201); }
			""";

		Assert.That(
			TextureSourceReferences.FindInCss(css),
			Is.EquivalentTo(new[] { "Assets/UI/Panel.png", "Assets/UI/Tile.png" }));
	}

	[Test]
	public void AssemblyScanFindsPathsInsideRazorMarkup()
	{
		var found = TextureSourceReferences.FindInAssembly(typeof(ImagePathTests).Assembly.Location);

		Assert.That(found, Does.Contain("Assets/UI/Potion.png"));
	}

	[Test]
	public void BackgroundDeclarationsParseAndKeepThePathsCase()
	{
		var node = new UiNode { Name = "div" };
		node.Attributes["class"] = "panel";
		var root = new UiNode { Name = "root" };
		root.Children.Add(node);
		var sheet = CssStyleSheet.Parse("""
			.panel {
				background-image: url("/Assets/UI/Panel.png");
				background-size: contain;
				background-position: right bottom;
				background-repeat: no-repeat;
			}
			""");

		sheet.Apply(root, 800, 600);

		Assert.Multiple(() =>
		{
			Assert.That(node.Style.BackgroundImage, Is.EqualTo("/Assets/UI/Panel.png"));
			Assert.That(node.Style.BackgroundSize.Kind, Is.EqualTo(UiBackgroundSizeKind.Contain));
			Assert.That(
				node.Style.BackgroundPosition,
				Is.EqualTo(new UiBackgroundPosition(new UiLength(100, UiLengthUnit.Percent), new UiLength(100, UiLengthUnit.Percent))));
			Assert.That(node.Style.BackgroundRepeat, Is.EqualTo(new UiBackgroundRepeat(false, false)));
			Assert.That(sheet.Diagnostics, Is.Empty);
		});
	}

	[Test]
	public void BackgroundDefaultsAreTheBrowsersAndInvalidValuesAreRejected()
	{
		Assert.Multiple(() =>
		{
			Assert.That(ComputedStyle.Default.BackgroundSize, Is.EqualTo(UiBackgroundSize.Auto));
			Assert.That(ComputedStyle.Default.BackgroundPosition, Is.EqualTo(UiBackgroundPosition.TopLeft));
			Assert.That(ComputedStyle.Default.BackgroundRepeat, Is.EqualTo(UiBackgroundRepeat.Both));
			Assert.That(UiBackgrounds.TryParseSize("-10px", out _), Is.False);
			Assert.That(UiBackgrounds.TryParsePosition("left left", out _), Is.False);
			Assert.That(UiBackgrounds.TryParseRepeat("round", out _), Is.False);
			Assert.That(UiBackgrounds.TryParseImage("linear-gradient(red, blue)", out _), Is.False);
			Assert.That(UiBackgrounds.TryParseImage("none", out var none) && none is null, Is.True);
		});
	}

	[Test]
	public void RepeatingTilesCoverTheBoxAndTheEdgeTileIsCut()
	{
		var tiles = new List<UiBackgroundTile>();

		var laidOut = UiBackgrounds.Layout(
			new Vector2(10, 10),
			new Vector2(110, 60),
			40,
			40,
			UiBackgroundSize.Auto,
			UiBackgroundPosition.TopLeft,
			UiBackgroundRepeat.Both,
			800,
			600,
			16,
			16,
			tiles);

		Assert.Multiple(() =>
		{
			Assert.That(laidOut, Is.True);
			Assert.That(tiles, Has.Count.EqualTo(6));
			Assert.That(tiles[0], Is.EqualTo(new UiBackgroundTile(new(10, 10), new(50, 50), Vector2.Zero, Vector2.One)));
			Assert.That(tiles[2].Max, Is.EqualTo(new Vector2(110, 50)));
			Assert.That(tiles[2].UvMax, Is.EqualTo(new Vector2(0.5f, 1)));
			Assert.That(tiles[5].Min, Is.EqualTo(new Vector2(90, 50)));
			Assert.That(tiles[5].UvMax, Is.EqualTo(new Vector2(0.5f, 0.25f)));
		});
	}

	[Test]
	public void CentredRepeatStartsATileBeforeTheBox()
	{
		var tiles = new List<UiBackgroundTile>();

		UiBackgrounds.Layout(
			Vector2.Zero,
			new Vector2(100, 40),
			40,
			40,
			UiBackgroundSize.Auto,
			new UiBackgroundPosition(new UiLength(50, UiLengthUnit.Percent), new UiLength(50, UiLengthUnit.Percent)),
			new UiBackgroundRepeat(true, false),
			800,
			600,
			16,
			16,
			tiles);

		// The centred tile spans 30..70, so the row runs -10..30, 30..70 and 70..110, cut to 0..100.
		Assert.That(
			tiles.Select(tile => (tile.Min.X, tile.Max.X)),
			Is.EqualTo(new[] { (0f, 30f), (30f, 70f), (70f, 100f) }));
		Assert.That(tiles[0].UvMin.X, Is.EqualTo(0.25f));
	}

	[Test]
	public void CoverAndContainScaleToTheBox()
	{
		var cover = new List<UiBackgroundTile>();
		var contain = new List<UiBackgroundTile>();
		var noRepeat = new UiBackgroundRepeat(false, false);
		var center = new UiBackgroundPosition(new UiLength(50, UiLengthUnit.Percent), new UiLength(50, UiLengthUnit.Percent));

		UiBackgrounds.Layout(Vector2.Zero, new Vector2(100, 50), 20, 20,
			new UiBackgroundSize(UiBackgroundSizeKind.Cover, UiLength.Auto, UiLength.Auto), center, noRepeat, 800, 600, 16, 16, cover);
		UiBackgrounds.Layout(Vector2.Zero, new Vector2(100, 50), 20, 20,
			new UiBackgroundSize(UiBackgroundSizeKind.Contain, UiLength.Auto, UiLength.Auto), center, noRepeat, 800, 600, 16, 16, contain);

		Assert.Multiple(() =>
		{
			Assert.That(cover.Single(), Is.EqualTo(new UiBackgroundTile(new(0, 0), new(100, 50), new(0, 0.25f), new(1, 0.75f))));
			Assert.That(contain.Single(), Is.EqualTo(new UiBackgroundTile(new(25, 0), new(75, 50), Vector2.Zero, Vector2.One)));
		});
	}

	[Test]
	public void ATileCountPastTheLimitDrawsNothing()
	{
		var tiles = new List<UiBackgroundTile>();

		var laidOut = UiBackgrounds.Layout(Vector2.Zero, new Vector2(1000, 1000), 1, 1,
			UiBackgroundSize.Auto, UiBackgroundPosition.TopLeft, UiBackgroundRepeat.Both, 800, 600, 16, 16, tiles);

		Assert.Multiple(() =>
		{
			Assert.That(laidOut, Is.False);
			Assert.That(tiles, Is.Empty);
		});
	}

	[Test]
	public void PathImagesAndBackgroundsResolveThroughTheProjectsTextureSources()
	{
		var potion = Register("Assets/UI/Potion.png", CreateTexture(40, 40));
		var panel = Register("Assets/UI/Panel.png", CreateTexture(20, 20));
		using var services = new ServiceCollection()
			.AddSingleton<ITextureSourceProvider>(_paths)
			.BuildServiceProvider();
		using var host = new GameplayUiHost(services);
		using var surface = host.Create<PathImageHud>(new UiSurfaceOptions(), "PathImageHud.css");

		Assert.That(host.TryConsumeLatest(out var frame), Is.True);

		try
		{
			var screen = frame.Screen;
			var commands = screen.Commands.Take(screen.CommandCount).ToArray();
			var panelCommand = commands.Single(command => ReferenceEquals(command.Image, panel));
			var potionCommand = commands.Single(command => ReferenceEquals(command.Image, potion));

			Assert.Multiple(() =>
			{
				// 40px tiles repeated across 100px: 40 + 40 + a half tile, in one row.
				Assert.That(panelCommand.ElemCount, Is.EqualTo(3 * 6));
				Assert.That(potionCommand.ElemCount, Is.EqualTo(6));
				Assert.That(
					Array.IndexOf(commands, panelCommand),
					Is.LessThan(Array.IndexOf(commands, potionCommand)),
					"The background is drawn under the element's children.");
			});
		}
		finally
		{
			frame.Release();
		}
	}

	private Texture Register(string path, Texture texture)
	{
		var id = Guid.NewGuid();
		_registry.Instances[id] = texture;
		_paths.Sources[path] = id;

		return texture;
	}

	private static Texture CreateTexture(int width, int height)
	{
		return new Texture(
			"UI test image",
			width,
			height,
			false,
			TextureFormat.Rgba8Unorm,
			[new TextureMipData(width, height, new byte[width * height * 4])]);
	}
}
