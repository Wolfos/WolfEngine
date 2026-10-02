using WolfEngine.Utility;

namespace WolfEngine.Editor.Projects;

public interface IFontAssetImporter
{
	TextureImportOperationResult ImportFont();
}

public sealed class FontAssetImporter(IFileDialogService dialogs, IEditorProjectService project,
	IProjectAssetPipelineService pipeline) : IFontAssetImporter
{
	public TextureImportOperationResult ImportFont()
	{
		if (!project.HasOpenProject) return TextureImportOperationResult.Failed("Open a project before importing fonts.");
		var path = dialogs.OpenFile(new FileDialogOptions { Title = "Import Font", AllowedExtensions = ["ttf", "otf"] });
		if (string.IsNullOrWhiteSpace(path)) return TextureImportOperationResult.CancelledByUser();
		try
		{
			pipeline.ImportExternalSource(project.ProjectRootPath!, path);
			project.ReloadAssetDatabaseFromIndex();
			return TextureImportOperationResult.Succeeded();
		}
		catch (Exception exception) { return TextureImportOperationResult.Failed($"Font import failed: {exception.Message}"); }
	}
}
