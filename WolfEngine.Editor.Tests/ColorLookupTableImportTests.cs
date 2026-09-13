using System.Globalization;
using System.Text;
using NSubstitute;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Importing;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class ColorLookupTableImportTests
{
	private string _projectRoot = string.Empty;

	[SetUp]
	public void SetUp()
	{
		_projectRoot = Path.Combine(Path.GetTempPath(), "WolfEngineLutImportTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_projectRoot);
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(_projectRoot))
		{
			Directory.Delete(_projectRoot, recursive: true);
		}
	}

	[Test]
	public void RebuildProject_ImportsCubeFile_AndResolvesVolumeLookupTable()
	{
		var pipeline = new ProjectAssetPipelineService(
			new AssetPipelineIndex(),
			new AssetMetadataStore(),
			Substitute.For<IImageLoader>(),
			new DataAssetStore(),
			new MaterialAssetStore(),
			Substitute.For<IThreeDFileImporter>(),
			Substitute.For<ITextureGpuCompressionService>(),
			new ProjectTypeCatalog(() => throw new InvalidOperationException("No gameplay types are needed.")));
		pipeline.InitializeProject(_projectRoot);
		File.WriteAllText(
			Path.Combine(AssetPipelinePaths.GetAssetsPath(_projectRoot), "Grade.cube"),
			"TITLE \"Grade\"\n" + IdentityLut(4));

		var database = pipeline.RebuildProject(_projectRoot);

		var asset = database.Assets.Single(entry => entry.Type == AssetType.ColorLookupTable);
		Assert.That(asset.Name, Is.EqualTo("Grade"));
		Assert.That(asset.TryGetSummary<ColorLookupTableAssetSummary>(out var summary), Is.True);
		Assert.That(summary!.Size, Is.EqualTo(4));
		Assert.That(summary.Title, Is.EqualTo("Grade"));
		Assert.That(asset.Artifacts.Count(artifact => artifact.Kind == ColorLookupTableArtifactSerializer.ArtifactKind),
			Is.EqualTo(1));

		var textureFactory = Substitute.For<ITextureFactory>();
		textureFactory.GetTexture(Arg.Any<Texture>()).Returns(call => call.Arg<Texture>());
		var resolver = new ColorLookupTableRuntimeAssetResolver(textureFactory);
		var lookupTable = (ColorLookupTable)resolver.Resolve(new RuntimeAssetResolveContext(
			asset.Id,
			asset,
			typeof(ColorLookupTable),
			_projectRoot,
			static (_, _) => null));

		Assert.That(lookupTable.Size, Is.EqualTo(4));
		Assert.That(lookupTable.Texture.Dimension, Is.EqualTo(TextureDimension.Texture3D));
		Assert.That(lookupTable.Texture.Depth, Is.EqualTo(4));
		Assert.That(lookupTable.Texture.Format, Is.EqualTo(TextureFormat.Rgba16Float));
		Assert.That(lookupTable.Texture.MipLevels[0].Data.Length,
			Is.EqualTo(TextureFormatUtilities.GetMipDataSize(TextureFormat.Rgba16Float, 4, 4, 4)));
	}

	private static string IdentityLut(int size)
	{
		var builder = new StringBuilder();
		builder.Append("LUT_3D_SIZE ").Append(size).Append('\n');
		for (var b = 0; b < size; b++)
		for (var g = 0; g < size; g++)
		for (var r = 0; r < size; r++)
		{
			builder.Append(string.Create(CultureInfo.InvariantCulture,
				$"{r / (float)(size - 1)} {g / (float)(size - 1)} {b / (float)(size - 1)}\n"));
		}

		return builder.ToString();
	}
}
