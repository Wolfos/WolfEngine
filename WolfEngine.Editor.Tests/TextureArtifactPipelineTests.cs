using NSubstitute;
using System.Text.Json;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
using WolfEngine.Importing;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Utility;

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
	public void ImportedTextureSerializer_TryReadMip_SelectsFirstMipAtOrBelowTargetSize()
	{
		using var tempDirectory = new TempDirectory();
		var path = Path.Combine(tempDirectory.Path, "texture.bin");
		var expectedData = CreateFilledBytes(32 * 32 * 4, 32);
		ImportedTextureSerializer.Write(
			path,
			new ImportedTexture(
				"texture",
				256,
				256,
				true,
				TextureSemantic.BaseColor,
				[
					new TextureMipData(256, 256, CreateFilledBytes(256 * 256 * 4, 255)),
					new TextureMipData(128, 128, CreateFilledBytes(128 * 128 * 4, 128)),
					new TextureMipData(64, 64, CreateFilledBytes(64 * 64 * 4, 64)),
					new TextureMipData(32, 32, expectedData)
				]));

		var read = ImportedTextureSerializer.TryReadMip(path, 64, out var preview);

		Assert.That(read, Is.True);
		Assert.That(preview.Width, Is.EqualTo(64));
		Assert.That(preview.Height, Is.EqualTo(64));
		Assert.That(preview.IsSrgb, Is.True);
		Assert.That(preview.Semantic, Is.EqualTo(TextureSemantic.BaseColor));
		Assert.That(preview.Data, Is.EqualTo(CreateFilledBytes(64 * 64 * 4, 64)));
	}

	[Test]
	public void ImportedTextureSerializer_TryReadMip_FallsBackToSmallestMip()
	{
		using var tempDirectory = new TempDirectory();
		var path = Path.Combine(tempDirectory.Path, "texture.bin");
		ImportedTextureSerializer.Write(
			path,
			new ImportedTexture(
				"texture",
				256,
				256,
				false,
				TextureSemantic.Unknown,
				[
					new TextureMipData(256, 256, CreateFilledBytes(256 * 256 * 4, 255)),
					new TextureMipData(128, 128, CreateFilledBytes(128 * 128 * 4, 128))
				]));

		var read = ImportedTextureSerializer.TryReadMip(path, 64, out var preview);

		Assert.That(read, Is.True);
		Assert.That(preview.Width, Is.EqualTo(128));
		Assert.That(preview.Height, Is.EqualTo(128));
		Assert.That(preview.Data, Is.EqualTo(CreateFilledBytes(128 * 128 * 4, 128)));
	}

	[Test]
	public void ImportedTextureSerializer_TryReadMip_RejectsInvalidOrMissingArtifacts()
	{
		using var tempDirectory = new TempDirectory();
		var invalidPath = Path.Combine(tempDirectory.Path, "invalid.bin");
		File.WriteAllText(invalidPath, "not a texture");

		Assert.That(ImportedTextureSerializer.TryReadMip(Path.Combine(tempDirectory.Path, "missing.bin"), 64, out _), Is.False);
		Assert.That(ImportedTextureSerializer.TryReadMip(invalidPath, 64, out _), Is.False);
	}

	[Test]
	public void AssetThumbnailLoader_UsesImportedTextureArtifactMip()
	{
		using var tempDirectory = new TempDirectory();
		var importedRelativePath = "Library/Imported/source/texture.bin";
		var importedPath = Path.Combine(tempDirectory.Path, importedRelativePath);
		ImportedTextureSerializer.Write(
			importedPath,
			new ImportedTexture(
				"texture",
				128,
				128,
				true,
				TextureSemantic.BaseColor,
				[
					new TextureMipData(128, 128, CreateFilledBytes(128 * 128 * 4, 128)),
					new TextureMipData(64, 64, CreateFilledBytes(64 * 64 * 4, 64))
				]));
		var projectService = Substitute.For<IEditorProjectService>();
		projectService.HasOpenProject.Returns(true);
		projectService.GetAbsolutePath(importedRelativePath).Returns(importedPath);
		var resources = new TestTextureResources();
		var renderer = Substitute.For<IRenderer>();
		Texture? uploadedTexture = null;
		renderer.CreateTextureResources(Arg.Do<Texture>(texture => uploadedTexture = texture)).Returns(resources);
		var loader = new AssetThumbnailLoader(projectService, renderer, new ImmediateMainThreadDispatcher());
		var asset = CreateTextureAsset(importedRelativePath);

		var loaded = loader.TryGetTextureThumbnailId(asset, out var textureId);

		Assert.That(loaded, Is.True);
		Assert.That(textureId, Is.EqualTo(resources.ShaderResourceView.Value));
		Assert.That(uploadedTexture, Is.Not.Null);
		Assert.That(uploadedTexture!.Width, Is.EqualTo(64));
		Assert.That(uploadedTexture.Height, Is.EqualTo(64));
		Assert.That(uploadedTexture.MipCount, Is.EqualTo(1));
		Assert.That(uploadedTexture.MipLevels[0].Data, Is.EqualTo(CreateFilledBytes(64 * 64 * 4, 64)));
	}

	[Test]
	public void AssetThumbnailLoader_MissingImportedArtifactReturnsFalse()
	{
		using var tempDirectory = new TempDirectory();
		var importedRelativePath = "Library/Imported/source/missing.bin";
		var projectService = Substitute.For<IEditorProjectService>();
		projectService.HasOpenProject.Returns(true);
		projectService.GetAbsolutePath(importedRelativePath).Returns(Path.Combine(tempDirectory.Path, importedRelativePath));
		var renderer = Substitute.For<IRenderer>();
		var loader = new AssetThumbnailLoader(projectService, renderer, new ImmediateMainThreadDispatcher());
		var asset = CreateTextureAsset(importedRelativePath);

		var loaded = loader.TryGetTextureThumbnailId(asset, out var textureId);

		Assert.That(loaded, Is.False);
		Assert.That(textureId, Is.Zero);
		renderer.DidNotReceiveWithAnyArgs().CreateTextureResources(default!);
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
			Artifacts =
			[
				new AssetArtifactRecord { ArtifactKey = "runtime-d3d12", Kind = "RuntimeTexture", Target = "d3d12", RelativePath = Path.GetFileName(d3dPath) },
				new AssetArtifactRecord { ArtifactKey = "runtime-metal", Kind = "RuntimeTexture", Target = "metal", RelativePath = Path.GetFileName(metalPath) }
			]
		};
		asset.SetSummary(new TextureAssetSummary());

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
			ImporterId = AssetImporterIds.Texture
		};
		metadata.SetImportSettings(new TextureImportSettings
		{
			TextureSemantic = TextureSemantic.BaseColorTransparent,
			MaxResolution = 2048
		});

		var store = new AssetMetadataStore();
		store.Save(metadataPath, metadata);
		var loaded = store.Load(metadataPath);

		Assert.That(loaded.TryGetImportSettings<TextureImportSettings>(out var loadedSettings), Is.True);
		Assert.That(loadedSettings.TextureSemantic, Is.EqualTo(TextureSemantic.BaseColorTransparent));
		Assert.That(loadedSettings.MaxResolution, Is.EqualTo(2048));
		Assert.That(loaded.ExtensionData, Is.Null);
	}

	[Test]
	public void TextureImportSettings_LoadsLegacyTextureMetadata()
	{
		using var tempDirectory = new TempDirectory();
		var metadataPath = Path.Combine(tempDirectory.Path, "texture.png.meta");
		var legacyMetadata = new
		{
			Version = AssetSourceMetaFile.CurrentVersion,
			SourceId = Guid.NewGuid(),
			ImporterId = AssetImporterIds.Texture,
			ImporterVersion = 1,
			TextureImportSettings = new TextureImportSettings
			{
				TextureSemantic = TextureSemantic.BaseColorTransparent,
				MaxResolution = 2048
			},
			SubAssets = Array.Empty<AssetSubAssetManifestEntry>()
		};
		File.WriteAllText(metadataPath, JsonSerializer.Serialize(legacyMetadata, AssetJson.SerializerOptions));

		var loaded = new AssetMetadataStore().Load(metadataPath);

		Assert.That(loaded.TryGetImportSettings<TextureImportSettings>(out var loadedSettings), Is.True);
		Assert.That(loadedSettings.TextureSemantic, Is.EqualTo(TextureSemantic.BaseColorTransparent));
		Assert.That(loadedSettings.MaxResolution, Is.EqualTo(2048));
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

	private static AssetDatabaseEntry CreateTextureAsset(string relativeImportedPath)
	{
		var asset = new AssetDatabaseEntry
		{
			Id = Guid.NewGuid(),
			Name = "texture",
			Type = AssetType.Texture2D,
			RelativeSourcePath = "Assets/texture.png"
		};
		asset.SetSummary(new TextureAssetSummary
		{
			RelativeSourceAssetPath = "Assets/texture.png",
			RelativeImportedPath = relativeImportedPath,
			Semantic = TextureSemantic.BaseColor
		});
		return asset;
	}

	private static byte[] CreateFilledBytes(int length, byte value)
	{
		var data = new byte[length];
		Array.Fill(data, value);
		return data;
	}

	private sealed class ImmediateMainThreadDispatcher : IMainThreadDispatcher
	{
		public bool IsMainThread => true;
		public void ExecutePending()
		{
		}

		public void Invoke(Action action)
		{
			action();
		}

		public T Invoke<T>(Func<T> action)
		{
			return action();
		}
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
