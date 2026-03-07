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

	private readonly IFileDialogService _fileDialogService;
	private readonly IEditorProjectService _projectService;
	private readonly ImportImageLoader _imageLoader;
	private readonly ITextureAssetMetaStore _textureAssetMetaStore;

	public TextureAssetImporter(
		IFileDialogService fileDialogService,
		IEditorProjectService projectService,
		ImportImageLoader imageLoader,
		ITextureAssetMetaStore textureAssetMetaStore)
	{
		_fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_imageLoader = imageLoader ?? throw new ArgumentNullException(nameof(imageLoader));
		_textureAssetMetaStore = textureAssetMetaStore ?? throw new ArgumentNullException(nameof(textureAssetMetaStore));
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

		var summary = new TextureAssetSummary
		{
			RelativeRawImagePath = relativeRawImagePath,
			Width = importedTexture.Width,
			Height = importedTexture.Height,
			Channels = importedTexture.Channels,
			IsSrgb = isSrgb,
			SourceExtension = Path.GetExtension(fileName).ToLowerInvariant()
		};

		var metaFile = new TextureAssetMetaFile
		{
			AssetId = assetId,
			SourceFileName = fileName,
			ImportSettings = new TextureImportSettings
			{
				IsSrgb = isSrgb,
				MaxResolution = 8192
			},
			Artifacts = new TextureImportArtifacts
			{
				RelativeRawImagePath = relativeRawImagePath
			},
			Summary = summary
		};

		var updatedDatabase = _projectService.CloneCurrentAssetDatabase();
		updatedDatabase.Assets.Add(new AssetDatabaseEntry
		{
			Id = assetId,
			Type = AssetType.Texture2D,
			Name = Path.GetFileNameWithoutExtension(fileName),
			RelativeAssetPath = relativeAssetPath,
			RelativeMetaPath = relativeMetaPath,
			TextureSummary = new TextureAssetSummary
			{
				RelativeRawImagePath = summary.RelativeRawImagePath,
				Width = summary.Width,
				Height = summary.Height,
				Channels = summary.Channels,
				IsSrgb = summary.IsSrgb,
				SourceExtension = summary.SourceExtension
			}
		});

		var createdFiles = new List<string>(3);
		try
		{
			File.Copy(sourcePath, destinationAssetPath, overwrite: false);
			createdFiles.Add(destinationAssetPath);

			_textureAssetMetaStore.Save(destinationMetaPath, metaFile);
			createdFiles.Add(destinationMetaPath);

			TextureRawImageSerializer.Write(destinationRawImagePath, importedTexture);
			createdFiles.Add(destinationRawImagePath);

			_projectService.SaveAssetDatabase(updatedDatabase);
			return TextureImportOperationResult.Succeeded();
		}
		catch
		{
			RollbackCreatedFiles(createdFiles);
			throw;
		}
	}

	private static string NormalizeRelativePath(string path)
	{
		return path.Replace('\\', '/');
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
