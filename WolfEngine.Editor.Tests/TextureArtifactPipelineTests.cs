using NSubstitute;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Importing;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Editor.Tests;

public sealed class TextureArtifactPipelineTests
{
	[Test]
	public void ApplyTextureData_KeepsPreviousGpuResourcesUntilReplacementUpload()
	{
		var texture = new Texture("texture", 1, 1, false, TextureFormat.Rgba8Unorm, [new TextureMipData(1, 1, [0, 0, 0, 255])]);
		var resources = new TestTextureResources();
		texture.MarkGpuResourcesCreated(resources);

		texture.ApplyTextureData(1, 1, false, TextureFormat.Rgba8Unorm, [new TextureMipData(1, 1, [255, 255, 255, 255])]);

		Assert.That(texture.HasGpuResources, Is.False);
		Assert.That(texture.Resources, Is.SameAs(resources));
	}

	[Test]
	public void TextureArtifactSerializer_RoundTripsCompressedMipChain()
	{
		using var tempDirectory = new TempDirectory();
		var path = Path.Combine(tempDirectory.Path, "texture.bin");
		var texture = new Texture(
			"artifact-texture",
			8,
			8,
			true,
			TextureFormat.Bc3Unorm,
			[
				new TextureMipData(8, 8, new byte[64]),
				new TextureMipData(4, 4, new byte[16]),
				new TextureMipData(2, 2, new byte[16]),
				new TextureMipData(1, 1, new byte[16])
			]);

		TextureArtifactSerializer.Write(path, texture, TextureSemantic.BaseColor, TextureCompressionFamily.Bc);
		var loaded = TextureArtifactSerializer.Read(path, texture.Name);

		Assert.That(loaded.Width, Is.EqualTo(texture.Width));
		Assert.That(loaded.Height, Is.EqualTo(texture.Height));
		Assert.That(loaded.IsSrgb, Is.EqualTo(texture.IsSrgb));
		Assert.That(loaded.Format, Is.EqualTo(texture.Format));
		Assert.That(loaded.MipCount, Is.EqualTo(texture.MipCount));
		Assert.That(loaded.MipLevels.Select(m => m.Data.Length), Is.EqualTo(texture.MipLevels.Select(m => m.Data.Length)));
	}

	[Test]
	public void TextureRuntimeAssetResolver_SelectsTargetSpecificArtifact()
	{
		using var tempDirectory = new TempDirectory();
		var d3dPath = Path.Combine(tempDirectory.Path, "runtime-d3d12.bin");
		var metalPath = Path.Combine(tempDirectory.Path, "runtime-metal.bin");
		TextureArtifactSerializer.Write(
			d3dPath,
			new Texture("test", 4, 4, false, TextureFormat.Bc1Unorm, [new TextureMipData(4, 4, new byte[8])]),
			TextureSemantic.BaseColor,
			TextureCompressionFamily.Bc);
		TextureArtifactSerializer.Write(
			metalPath,
			new Texture("test", 4, 4, false, TextureFormat.Bc3Unorm, [new TextureMipData(4, 4, new byte[16])]),
			TextureSemantic.BaseColor,
			TextureCompressionFamily.Bc);

		var factory = Substitute.For<ITextureFactory>();
		factory.GetTexture(Arg.Any<Texture>()).Returns(call => call.Arg<Texture>());
		var targetProvider = Substitute.For<IRuntimeArtifactTargetProvider>();
		targetProvider.CurrentTarget.Returns("metal");
		var resolver = new TextureRuntimeAssetResolver(factory, targetProvider);

		var asset = new AssetDatabaseEntry
		{
			Id = Guid.NewGuid(),
			Name = "test",
			Type = AssetType.Texture2D,
			TextureSummary = new TextureAssetSummary(),
			Artifacts =
			[
				new AssetArtifactRecord { ArtifactKey = "runtime-d3d12", Kind = "RuntimeTexture", Target = "d3d12", RelativePath = Path.GetFileName(d3dPath) },
				new AssetArtifactRecord { ArtifactKey = "runtime-metal", Kind = "RuntimeTexture", Target = "metal", RelativePath = Path.GetFileName(metalPath) }
			]
		};

		var resolved = (Texture)resolver.Resolve(new RuntimeAssetResolveContext(
			asset.Id,
			asset,
			typeof(Texture),
			tempDirectory.Path,
			(_, _) => null))!;

		Assert.That(resolved.Format, Is.EqualTo(TextureFormat.Bc3Unorm));
		factory.Received(1).GetTexture(Arg.Is<Texture>(texture => texture.Format == TextureFormat.Bc3Unorm));
	}

	[Test]
	public void TextureImportSettings_RoundTripSemantic()
	{
		using var tempDirectory = new TempDirectory();
		var metadataPath = Path.Combine(tempDirectory.Path, "texture.png.meta");
		var metadata = new AssetSourceMetaFile
		{
			SourceId = Guid.NewGuid(),
			ImporterId = AssetImporterIds.Texture,
			TextureImportSettings = new TextureImportSettings
			{
				TextureSemantic = TextureSemantic.BaseColorTransparent,
				MaxResolution = 2048
			}
		};

		var store = new AssetMetadataStore();
		store.Save(metadataPath, metadata);
		var loaded = store.Load(metadataPath);

		Assert.That(loaded.TextureImportSettings, Is.Not.Null);
		Assert.That(loaded.TextureImportSettings!.TextureSemantic, Is.EqualTo(TextureSemantic.BaseColorTransparent));
		Assert.That(loaded.TextureImportSettings.MaxResolution, Is.EqualTo(2048));
	}

	[TestCase(TextureSemantic.Unknown, false, TextureFormat.Unknown)]
	[TestCase(TextureSemantic.BaseColor, true, TextureFormat.Bc1Unorm)]
	[TestCase(TextureSemantic.BaseColorTransparent, true, TextureFormat.Bc3Unorm)]
	[TestCase(TextureSemantic.Normal, true, TextureFormat.Bc5Unorm)]
	[TestCase(TextureSemantic.Occlusion, true, TextureFormat.Bc4Unorm)]
	public void TextureCompressionCompiler_MapsSemanticsToExpectedRuntimeFormat(TextureSemantic semantic, bool expectedResult, TextureFormat expectedFormat)
	{
		var result = TextureCompressionCompiler.TryGetBcRuntimeFormat(semantic, out var format);

		Assert.That(result, Is.EqualTo(expectedResult));
		Assert.That(format, Is.EqualTo(expectedFormat));
	}

	private sealed class TestTextureResources : ITextureResources
	{
		public IGfxTexture Texture { get; } = Substitute.For<IGfxTexture>();
		public DescriptorHandle ShaderResourceView { get; } = new(DescriptorKind.ShaderResourceView, 42);
	}

	private sealed class TempDirectory : IDisposable
	{
		public TempDirectory()
		{
			Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WolfEngineTests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path);
		}

		public string Path { get; }

		public void Dispose()
		{
			if (Directory.Exists(Path))
			{
				Directory.Delete(Path, recursive: true);
			}
		}
	}
}
