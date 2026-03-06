using System.Text.Json;
using System.Text.Json.Serialization;
using WolfEngine.AssetPipeline;
using WolfEngine.Importing;
using WolfEngine.Utility;
using ImportImageLoader = WolfEngine.Importing.IImageLoader;

namespace WolfEngine.Editor.Projects;

public readonly record struct TextureImportOperationResult(bool Success, bool Cancelled, string? ErrorMessage)
{
	public static TextureImportOperationResult Succeeded() => new(true, false, null);
	public static TextureImportOperationResult CancelledByUser() => new(false, true, null);
	public static TextureImportOperationResult Failed(string errorMessage) => new(false, false, errorMessage);
}

public interface ITextureAssetImporter
{
	TextureImportOperationResult ImportTexture();
}

public sealed class TextureAssetImporter : ITextureAssetImporter
{
	private static readonly string[] SupportedExtensions =
	[
		"jpg",
		"jpeg",
		"png",
		"bmp",
		"tga",
		"psd",
		"gif",
		"hdr"
	];

	private static readonly JsonSerializerOptions MetaJsonOptions = new()
	{
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter() }
	};

	private readonly IFileDialogService _fileDialogService;
	private readonly IEditorProjectService _projectService;
	private readonly ImportImageLoader _imageLoader;
	private readonly IAssetDatabaseStore _assetDatabaseStore;

	public TextureAssetImporter(
		IFileDialogService fileDialogService,
		IEditorProjectService projectService,
		ImportImageLoader imageLoader,
		IAssetDatabaseStore assetDatabaseStore)
	{
		_fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_imageLoader = imageLoader ?? throw new ArgumentNullException(nameof(imageLoader));
		_assetDatabaseStore = assetDatabaseStore ?? throw new ArgumentNullException(nameof(assetDatabaseStore));
	}

	public TextureImportOperationResult ImportTexture()
	{
		if (_projectService.HasOpenProject == false)
		{
			return TextureImportOperationResult.Failed("Open or create a project before importing textures.");
		}

		var sourcePath = _fileDialogService.OpenFile(new FileDialogOptions
		{
			Title = "Import Texture",
			AllowedExtensions = SupportedExtensions
		});
		if (string.IsNullOrWhiteSpace(sourcePath))
		{
			return TextureImportOperationResult.CancelledByUser();
		}

		try
		{
			return ImportTextureFromPath(sourcePath);
		}
		catch (Exception ex)
		{
			return TextureImportOperationResult.Failed($"Failed to import texture: {ex.Message}");
		}
	}

	private TextureImportOperationResult ImportTextureFromPath(string sourcePath)
	{
		var importedTexture = _imageLoader.Load(sourcePath, TextureSemantic.BaseColor);
		const bool isSrgb = true;

		var fileName = Path.GetFileName(sourcePath);
		var destinationAssetPath = Path.Combine(_projectService.AssetsPath!, fileName);
		if (File.Exists(destinationAssetPath))
		{
			return TextureImportOperationResult.Failed(
				$"An asset named '{fileName}' already exists in the project Assets folder.");
		}

		var assetId = Guid.NewGuid();
		var relativeAssetPath = NormalizeRelativePath(Path.Combine("Assets", fileName));
		var relativeMetaPath = NormalizeRelativePath(relativeAssetPath + ".meta.json");
		var relativeRawImagePath = NormalizeRelativePath(Path.Combine("Database", $"{assetId:D}.bin"));
		var destinationMetaPath = _projectService.GetAbsolutePath(relativeMetaPath);
		var destinationRawImagePath = _projectService.GetAbsolutePath(relativeRawImagePath);

		var metaFile = new TextureAssetMetaFile
		{
			AssetId = assetId,
			AssetType = AssetType.Texture2D,
			SourceFileName = fileName,
			ImportSettings = new TextureImportSettings
			{
				IsSrgb = isSrgb
			},
			ImportResult = new TextureImportResultMetadata
			{
				Width = importedTexture.Width,
				Height = importedTexture.Height,
				Channels = importedTexture.Channels,
				RelativeRawImagePath = relativeRawImagePath
			}
		};

		var updatedDatabase = CloneDatabase(_projectService.CurrentAssetDatabase);
		updatedDatabase.Assets.Add(new AssetDatabaseEntry
		{
			Id = assetId,
			Type = AssetType.Texture2D,
			Name = Path.GetFileNameWithoutExtension(fileName),
			RelativeAssetPath = relativeAssetPath,
			RelativeMetaPath = relativeMetaPath,
			RelativeRawImagePath = relativeRawImagePath,
			Width = importedTexture.Width,
			Height = importedTexture.Height,
			Channels = importedTexture.Channels,
			IsSrgb = isSrgb,
			SourceExtension = Path.GetExtension(fileName).ToLowerInvariant()
		});

		var databaseFilePath = Path.Combine(_projectService.DatabasePath!, AssetDatabase.FileName);
		var createdFiles = new List<string>(3);
		try
		{
			File.Copy(sourcePath, destinationAssetPath, overwrite: false);
			createdFiles.Add(destinationAssetPath);

			WriteTextAtomically(destinationMetaPath, JsonSerializer.Serialize(metaFile, MetaJsonOptions));
			createdFiles.Add(destinationMetaPath);

			TextureRawImageSerializer.Write(destinationRawImagePath, importedTexture);
			createdFiles.Add(destinationRawImagePath);

			_assetDatabaseStore.Save(databaseFilePath, updatedDatabase);
			_projectService.ReloadAssetDatabase();
			return TextureImportOperationResult.Succeeded();
		}
		catch
		{
			RollbackCreatedFiles(createdFiles);
			throw;
		}
	}

	private static AssetDatabase CloneDatabase(AssetDatabase source)
	{
		return new AssetDatabase
		{
			Version = source.Version,
			Assets = source.Assets.Select(asset => new AssetDatabaseEntry
			{
				Id = asset.Id,
				Type = asset.Type,
				Name = asset.Name,
				RelativeAssetPath = asset.RelativeAssetPath,
				RelativeMetaPath = asset.RelativeMetaPath,
				RelativeRawImagePath = asset.RelativeRawImagePath,
				Width = asset.Width,
				Height = asset.Height,
				Channels = asset.Channels,
				IsSrgb = asset.IsSrgb,
				SourceExtension = asset.SourceExtension
			}).ToList()
		};
	}

	private static string NormalizeRelativePath(string path)
	{
		return path.Replace('\\', '/');
	}

	private static void WriteTextAtomically(string path, string content)
	{
		var directory = Path.GetDirectoryName(path);
		if (string.IsNullOrWhiteSpace(directory) == false)
		{
			Directory.CreateDirectory(directory);
		}

		var tempPath = path + ".tmp";
		File.WriteAllText(tempPath, content);
		File.Move(tempPath, path, true);
	}

	private static void RollbackCreatedFiles(IEnumerable<string> files)
	{
		foreach (var file in files.Reverse())
		{
			try
			{
				if (File.Exists(file))
				{
					File.Delete(file);
				}
			}
			catch
			{
			}
		}
	}
}
