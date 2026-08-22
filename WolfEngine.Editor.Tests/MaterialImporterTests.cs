using System.Reflection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Importing;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class MaterialImporterTests
{
	[TestCase(MaterialAssetType.Opaque, TextureSemantic.BaseColor)]
	[TestCase(MaterialAssetType.AlphaTest, TextureSemantic.BaseColorTransparent)]
	[TestCase(MaterialAssetType.AlphaBlend, TextureSemantic.BaseColorTransparent)]
	public void ImportMaterial_WritesExpectedTextureSemantics(MaterialAssetType materialType, TextureSemantic expectedAlbedoSemantic)
	{
		using var tempDirectory = new TempDirectory();
		var projectRoot = tempDirectory.Path;
		var projectService = new TestProjectService(projectRoot);
		var metadataStore = new AssetMetadataStore();
		var materialStore = new MaterialAssetStore();
		var importer = new MaterialImporter(projectService, materialStore, metadataStore);

		var sourceDirectory = Path.Combine(projectRoot, "SourceTextures");
		Directory.CreateDirectory(sourceDirectory);
		var albedoPath = CreateTexture(sourceDirectory, "albedo.png", new Rgba32(255, 0, 0, 255));
		var normalPath = CreateTexture(sourceDirectory, "normal.png", new Rgba32(128, 128, 255, 255));
		var metallicPath = CreateTexture(sourceDirectory, "metallic.png", new Rgba32(0, 0, 64, 255));
		var roughnessPath = CreateTexture(sourceDirectory, "roughness.png", new Rgba32(0, 128, 0, 255));
		var emissivePath = CreateTexture(sourceDirectory, "emissive.png", new Rgba32(16, 32, 64, 255));
		var occlusionPath = CreateTexture(sourceDirectory, "occlusion.png", new Rgba32(200, 0, 0, 255));

		var result = importer.ImportMaterial(new MaterialImportRequest
		{
			MaterialName = $"Material_{materialType}",
			MaterialType = materialType,
			AlbedoPath = albedoPath,
			NormalPath = normalPath,
			MetallicPath = metallicPath,
			RoughnessPath = roughnessPath,
			EmissivePath = emissivePath,
			OcclusionPath = occlusionPath
		});

		Assert.That(result.Success, Is.True, result.ErrorMessage);
		Assert.That(projectService.ReloadCalls, Is.EqualTo(1));

		var importDirectory = Path.Combine(projectRoot, "Assets", "Imported", $"Material_{materialType}");
		AssertTextureSemantic(metadataStore, Path.Combine(importDirectory, $"Material_{materialType}_Albedo.png.meta"), expectedAlbedoSemantic);
		AssertTextureSemantic(metadataStore, Path.Combine(importDirectory, $"Material_{materialType}_Normal.png.meta"), TextureSemantic.Normal);
		AssertTextureSemantic(metadataStore, Path.Combine(importDirectory, $"Material_{materialType}_ORM.png.meta"), TextureSemantic.MetallicRoughness);
		AssertTextureSemantic(metadataStore, Path.Combine(importDirectory, $"Material_{materialType}_Emissive.png.meta"), TextureSemantic.Emissive);
	}

	[Test]
	public void ProjectAssetPipelineService_CreateOrmImportedTexture_PacksOcclusionRoughnessAndMetallic()
	{
		var metallicRoughness = new ImportedTexture(
			"mr.png",
			1,
			1,
			false,
			TextureSemantic.MetallicRoughness,
			[new TextureMipData(1, 1, [11, 128, 64, 255])]);
		var occlusion = new ImportedTexture(
			"ao.png",
			1,
			1,
			false,
			TextureSemantic.Occlusion,
			[new TextureMipData(1, 1, [200, 7, 9, 255])]);
		var method = typeof(ProjectAssetPipelineService).GetMethod(
			"CreateOrmImportedTexture",
			BindingFlags.NonPublic | BindingFlags.Static)
			?? throw new AssertionException("CreateOrmImportedTexture method was not found.");

		var ormTexture = (ImportedTexture)method.Invoke(null, [metallicRoughness, occlusion, 0])!;

		Assert.That(ormTexture.Semantic, Is.EqualTo(TextureSemantic.MetallicRoughness));
		Assert.That(ormTexture.PixelData, Is.EqualTo(new byte[] { 200, 128, 64, 255 }));
	}

	private static void AssertTextureSemantic(AssetMetadataStore metadataStore, string metaPath, TextureSemantic expectedSemantic)
	{
		var metadata = metadataStore.Load(metaPath);
		Assert.That(metadata.TryGetImportSettings<TextureImportSettings>(out var settings), Is.True);
		Assert.That(settings.TextureSemantic, Is.EqualTo(expectedSemantic));
	}

	private static string CreateTexture(string directory, string fileName, Rgba32 color)
	{
		var path = Path.Combine(directory, fileName);
		using var image = new Image<Rgba32>(2, 2, color);
		image.SaveAsPng(path);
		return path;
	}

	private sealed class TestProjectService : IEditorProjectService
	{
		private readonly string _projectRootPath;

		public TestProjectService(string projectRootPath)
		{
			_projectRootPath = projectRootPath;
			Directory.CreateDirectory(Path.Combine(projectRootPath, AssetPipelinePaths.AssetsFolderName));
			Directory.CreateDirectory(Path.Combine(projectRootPath, AssetPipelinePaths.LibraryFolderName));
		}

		public int ReloadCalls { get; private set; }
		public bool HasOpenProject => true;
		public string? ProjectRootPath => _projectRootPath;
		public string? AssetsPath => Path.Combine(_projectRootPath, AssetPipelinePaths.AssetsFolderName);
		public string? LibraryPath => Path.Combine(_projectRootPath, AssetPipelinePaths.LibraryFolderName);
		public string? DatabasePath => LibraryPath;
		public string? GameplayProjectRelativePath => null;
		public string? GameplayProjectPath => null;
		public AssetDatabase CurrentAssetDatabase { get; } = new();

		public bool CreateProject(string parentFolder, string projectName, out string errorMessage) => throw new NotSupportedException();
		public bool OpenProject(string projectRoot, out string errorMessage) => throw new NotSupportedException();
		public void CloseProject() => throw new NotSupportedException();

		public AssetDatabaseRefreshResult ReloadAssetDatabase()
		{
			ReloadCalls++;
			return AssetDatabaseRefreshResult.Empty;
		}

		public void ReloadAssetDatabaseFromIndex() => throw new NotSupportedException();
		public void RefreshAssetSource(string relativeSourcePath) => throw new NotSupportedException();
		public void SaveAssetDatabase(AssetDatabase database) => throw new NotSupportedException();
		public AssetDatabase CloneCurrentAssetDatabase() => throw new NotSupportedException();
		public bool TryGetAsset(Guid assetId, out AssetDatabaseEntry asset) => throw new NotSupportedException();
		public string GetAbsolutePath(string relativePath) => Path.Combine(_projectRootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
		public void DeleteAssetSource(string relativeSourcePath) => throw new NotSupportedException();
		public void DeleteFolder(string relativeFolderPath) => throw new NotSupportedException();
		public string RenameAssetSource(string relativeSourcePath, string newName) => throw new NotSupportedException();
		public string RenameFolder(string relativeFolderPath, string newName) => throw new NotSupportedException();
		public string MoveAssetSourceToFolder(string relativeSourcePath, string targetFolderPath) => throw new NotSupportedException();
		public string MoveFolderToFolder(string relativeFolderPath, string targetFolderPath) => throw new NotSupportedException();
		public string CreateFolder(string parentFolderPath, string folderName) => throw new NotSupportedException();
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
