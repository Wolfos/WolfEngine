using System;
using WolfEngine.Utility;

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
	private readonly IProjectAssetPipelineService _assetPipelineService;

	public TextureAssetImporter(
		IFileDialogService fileDialogService,
		IEditorProjectService projectService,
		IProjectAssetPipelineService assetPipelineService)
	{
		_fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_assetPipelineService = assetPipelineService ?? throw new ArgumentNullException(nameof(assetPipelineService));
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
			_assetPipelineService.ImportExternalSource(_projectService.ProjectRootPath!, sourcePath);
			_projectService.ReloadAssetDatabaseFromIndex();
			return TextureImportOperationResult.Succeeded();
		}
		catch (Exception ex)
		{
			return TextureImportOperationResult.Failed($"Failed to import texture: {ex.Message}");
		}
	}
}
