using WolfEngine.Audio;
using WolfEngine.AssetPipeline;
using WolfEngine.Utility;

namespace WolfEngine.Editor.Projects;

public interface IAudioAssetImporter
{
	TextureImportOperationResult ImportAudio();
}

public sealed class AudioAssetImporter : IAudioAssetImporter
{
	private readonly IFileDialogService _dialogs;
	private readonly IEditorProjectService _project;
	private readonly IProjectAssetPipelineService _pipeline;

	public AudioAssetImporter(IFileDialogService dialogs, IEditorProjectService project, IProjectAssetPipelineService pipeline)
	{
		_dialogs = dialogs;
		_project = project;
		_pipeline = pipeline;
	}

	public TextureImportOperationResult ImportAudio()
	{
		if (!_project.HasOpenProject) return TextureImportOperationResult.Failed("Open or create a project before importing audio.");
		var path = _dialogs.OpenFile(new FileDialogOptions { Title = "Import Audio", AllowedExtensions = ["wav", "flac", "mp3", "ogg"] });
		if (string.IsNullOrWhiteSpace(path)) return TextureImportOperationResult.CancelledByUser();
		try
		{
			_pipeline.ImportExternalSource(_project.ProjectRootPath!, path);
			_project.ReloadAssetDatabaseFromIndex();
			return TextureImportOperationResult.Succeeded();
		}
		catch (Exception ex) { return TextureImportOperationResult.Failed($"Failed to import audio: {ex.Message}"); }
	}
}

public sealed class EditorAudioContentProvider : IAudioContentProvider
{
	private readonly IEditorProjectService _project;
	public EditorAudioContentProvider(IEditorProjectService project) => _project = project;
	public AudioContentStream Open(Guid assetId)
	{
		if (!_project.TryGetAsset(assetId, out var asset) || asset.Type != AssetType.AudioClip)
			throw new KeyNotFoundException($"Audio asset '{assetId:D}' was not found.");
		var artifact = asset.Artifacts.FirstOrDefault(item =>
			string.Equals(item.Kind, AudioAssetConstants.RuntimeArtifactKind, StringComparison.Ordinal));
		if (artifact is null) throw new InvalidDataException($"Audio asset '{assetId:D}' has no runtime artifact.");
		return AudioArtifact.Open(_project.GetAbsolutePath(artifact.RelativePath));
	}
}
