using Microsoft.Data.Sqlite;
using NSubstitute;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.Tooling.Fonts;
using WolfEngine.Importing;

namespace WolfEngine.Editor.Tests;

public sealed class FontImportTests
{
	[Test]
	public void InterImportProducesDeterministicMtsdfAndPreservesAssetIdentity()
	{
		var root = Path.Combine(Path.GetTempPath(), "WolfFontImport-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var pipeline = new ProjectAssetPipelineService(new AssetPipelineIndex(), new AssetMetadataStore(),
				Substitute.For<IImageLoader>(), new DataAssetStore(), new MaterialAssetStore(),
				Substitute.For<IThreeDFileImporter>(), null!, fontCompiler: new FontCompiler());
			pipeline.InitializeProject(root);
			File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures/Inter.ttf"), Path.Combine(root, "Assets/Inter.ttf"));
			var database = pipeline.RefreshProjectIncremental(root);
			var asset = database.Assets.Single(a => a.Type == AssetType.Font);
			var path = Path.Combine(root, asset.RelativeAssetPath);
			var font = FontArtifactSerializer.Read(path);
			Assert.That(font.Metrics.GlyphCount, Is.GreaterThanOrEqualTo(95));
			Assert.That(font.Metrics.UnitsPerEm, Is.GreaterThan(0));
			Assert.That(font.Metrics.Ascender, Is.GreaterThan(0));
			Assert.That(font.Metrics.Descender, Is.LessThan(0));
			Assert.That(font.PixelsRgba.Length, Is.EqualTo(font.Metrics.Width * font.Metrics.Height * 4));
			var space = font.Glyphs.Single(g => g.Id == font.CharacterMap[' ']);
			Assert.That(space.Advance, Is.GreaterThan(0));
			Assert.That(space.AtlasWidth, Is.Zero);
			var o = font.Glyphs.Single(g => g.Id == font.CharacterMap['O']);
			float Alpha(int x, int y) => font.PixelsRgba[((o.AtlasY + y) * font.Metrics.Width + o.AtlasX + x) * 4 + 3] / 255f;
			Assert.That(Alpha(0, 0), Is.LessThan(0.5), "The exterior must be outside the glyph.");
			Assert.That(Alpha(o.AtlasWidth / 2, o.AtlasHeight / 2), Is.LessThan(0.5), "The O counter must remain open.");
			Assert.That(font.PixelsRgba.Where((_, i) => i % 4 == 3).Max(), Is.GreaterThan(127), "The glyph interiors must have positive distance.");
			var t = font.Glyphs.Single(g => g.Id == font.CharacterMap['t']);
			var tx = (int)MathF.Floor((0.17f - t.Left) * font.Metrics.PixelsPerEm);
			var ty = (int)MathF.Floor((t.Top - 0.5f) * font.Metrics.PixelsPerEm);
			Assert.That(font.PixelsRgba[((t.AtlasY + ty) * font.Metrics.Width + t.AtlasX + tx) * 4 + 3],
				Is.GreaterThan(180), "An internal stem/crossbar overlap must not leave a distance-field seam.");
			// Verify RGB median and true SDF describe the same contour; detects coordinate-convention regressions.
			var mismatches = 0;
			for (var p = 0; p < font.PixelsRgba.Length; p += 4)
			{
				var r = font.PixelsRgba[p]; var g = font.PixelsRgba[p + 1]; var b = font.PixelsRgba[p + 2];
				var median = Math.Max(Math.Min(r, g), Math.Min(Math.Max(r, g), b));
				var alpha = font.PixelsRgba[p + 3];
				if (Math.Abs(alpha - 127) > 16 && (median > 127) != (alpha > 127)) mismatches++;
			}
			Assert.That(mismatches, Is.LessThan(font.PixelsRgba.Length / 4 / 100), "MSDF and SDF silhouettes must agree away from edges.");
			var hash = AssetHashing.ComputeFileHash(path);
			pipeline.ReimportSource(root, "Assets/Inter.ttf");
			Assert.That(pipeline.LoadDatabase(root).Assets.Single(a => a.Type == AssetType.Font).Id, Is.EqualTo(asset.Id));
			Assert.That(AssetHashing.ComputeFileHash(path), Is.EqualTo(hash));
			Assert.That(pipeline.RefreshProjectIncrementalWithChanges(root).ReimportedNodeIds, Is.Empty);
			var output = Environment.GetEnvironmentVariable("WOLF_FONT_VALIDATION_OUTPUT");
			if (!string.IsNullOrEmpty(output))
			{
				Directory.CreateDirectory(output);
				foreach (var artifact in asset.Artifacts)
					File.Copy(Path.Combine(root, artifact.RelativePath), Path.Combine(output, Path.GetFileName(artifact.RelativePath)), overwrite: true);
			}
		}
		finally
		{
			SqliteConnection.ClearAllPools();
			Directory.Delete(root, recursive: true);
		}
	}

	[TestCase(0, 6, 512)]
	[TestCase(48, 0, 512)]
	[TestCase(48, 6, 500)]
	[TestCase(129, 6, 512)]
	public void InvalidBakeSettingsAreRejectedBeforeOpeningTheSource(int pixelsPerEm, int range, int width)
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new FontCompiler().Compile("not-a-font.ttf", "unused",
			new FontImportSettings { PixelsPerEm = pixelsPerEm, DistanceRange = range, AtlasWidth = width }));
	}
}
