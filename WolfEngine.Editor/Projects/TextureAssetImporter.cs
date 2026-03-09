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
	private readonly ITextureAssetStore _textureAssetStore;

	public TextureAssetImporter(
		IFileDialogService fileDialogService,
		IEditorProjectService projectService,
		ImportImageLoader imageLoader,
		ITextureAssetStore textureAssetStore)
	{
		_fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_imageLoader = imageLoader ?? throw new ArgumentNullException(nameof(imageLoader));
		_textureAssetStore = textureAssetStore ?? throw new ArgumentNullException(nameof(textureAssetStore));
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
		var fileName = Path.GetFileName(sourcePath);
		var assetId = Guid.NewGuid();
		var sourceExtension = Path.GetExtension(fileName).ToLowerInvariant();
		var assetName = GetNextTextureName(Path.GetFileNameWithoutExtension(fileName), sourceExtension);
		var relativeAssetPath = _textureAssetStore.GetAssetRelativePath(assetName);
		var relativeSourceAssetPath = _textureAssetStore.GetSourceRelativePath(assetName, sourceExtension);
		var relativeStatePath = _textureAssetStore.GetStateRelativePath(assetId);
		var relativeRuntimeArtifactPath = _textureAssetStore.GetRuntimeArtifactRelativePath(assetId);
		var absoluteAssetPath = _projectService.GetAbsolutePath(relativeAssetPath);
		var absoluteSourceAssetPath = _projectService.GetAbsolutePath(relativeSourceAssetPath);
		var absoluteStatePath = _projectService.GetAbsolutePath(relativeStatePath);
		var absoluteRuntimeArtifactPath = _projectService.GetAbsolutePath(relativeRuntimeArtifactPath);

		var summary = new TextureAssetSummary
		{
			RelativeSourceAssetPath = relativeSourceAssetPath,
			RelativeRawImagePath = relativeRuntimeArtifactPath,
			Width = importedTexture.Width,
			Height = importedTexture.Height,
			Channels = importedTexture.Channels,
			IsSrgb = importedTexture.IsSrgb,
			SourceExtension = sourceExtension
		};

		var textureAsset = _textureAssetStore.Create(relativeSourceAssetPath, new TextureImportSettings
		{
			IsSrgb = importedTexture.IsSrgb,
			MaxResolution = 8192
		});
		var textureState = _textureAssetStore.CreateState(assetId, summary, _textureAssetStore.CreateDefaultRuntimeArtifacts(assetId));

		var updatedDatabase = _projectService.CloneCurrentAssetDatabase();
		updatedDatabase.Assets.Add(new AssetDatabaseEntry
		{
			Id = assetId,
			Type = AssetType.Texture2D,
			Name = assetName,
			RelativeAssetPath = relativeAssetPath,
			RelativeStatePath = relativeStatePath,
			RelativeMetaPath = relativeStatePath,
			TextureSummary = new TextureAssetSummary
			{
				RelativeSourceAssetPath = summary.RelativeSourceAssetPath,
				RelativeRawImagePath = summary.RelativeRawImagePath,
				Width = summary.Width,
				Height = summary.Height,
				Channels = summary.Channels,
				IsSrgb = summary.IsSrgb,
				SourceExtension = summary.SourceExtension
			}
		});

		var createdFiles = new List<string>(4);
		try
		{
			File.Copy(sourcePath, absoluteSourceAssetPath, overwrite: false);
			createdFiles.Add(absoluteSourceAssetPath);

			_textureAssetStore.SaveAsset(absoluteAssetPath, textureAsset);
			createdFiles.Add(absoluteAssetPath);

			_textureAssetStore.SaveState(absoluteStatePath, textureState);
			createdFiles.Add(absoluteStatePath);

			TextureRawImageSerializer.Write(absoluteRuntimeArtifactPath, importedTexture);
			createdFiles.Add(absoluteRuntimeArtifactPath);

			_projectService.SaveAssetDatabase(updatedDatabase);
			return TextureImportOperationResult.Succeeded();
		}
		catch
		{
			RollbackCreatedFiles(createdFiles);
			throw;
		}
	}

	private string GetNextTextureName(string baseName, string sourceExtension)
	{
		baseName = string.IsNullOrWhiteSpace(baseName) ? "New Texture" : baseName.Trim();
		var index = 0;
		while (true)
		{
			var candidateName = index == 0 ? baseName : $"{baseName} {index}";
			var relativeAssetPath = _textureAssetStore.GetAssetRelativePath(candidateName);
			var relativeSourcePath = _textureAssetStore.GetSourceRelativePath(candidateName, sourceExtension);
			var absoluteAssetPath = _projectService.GetAbsolutePath(relativeAssetPath);
			var absoluteSourcePath = _projectService.GetAbsolutePath(relativeSourcePath);
			if (File.Exists(absoluteAssetPath) == false && File.Exists(absoluteSourcePath) == false)
			{
				return candidateName;
			}

			index++;
		}
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
