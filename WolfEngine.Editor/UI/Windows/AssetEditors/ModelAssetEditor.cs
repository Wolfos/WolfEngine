using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

public sealed class ModelAssetEditor
{
	private readonly IEditorProjectService _projectService;
	private readonly IAssetMetadataStore _metadataStore;
	private AssetSourceMetaFile? _loadedMetadata;
	private Guid? _loadedModelAssetId;

	public ModelAssetEditor(IEditorProjectService projectService, IAssetMetadataStore metadataStore)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_metadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
	}

	public void Draw(AssetDatabaseEntry asset)
	{
		if (asset.TryGetSummary<Model3DAssetSummary>(out var modelSummary) == false)
		{
			ImGui.TextUnformatted("Model summary is unavailable.");
			return;
		}

		ImGui.TextUnformatted($"Root nodes: {modelSummary.RootNodeCount}");
		ImGui.TextUnformatted($"Skeletons: {modelSummary.SkeletonCount} | Animations: {modelSummary.AnimationCount}");

		var metadata = EnsureMetadataLoaded(asset);
		if (metadata is null)
		{
			ImGui.TextUnformatted("Failed to load model metadata.");
			return;
		}

		var modelImportSettings = metadata.GetImportSettingsOrDefault(() => new ModelImportSettings());
		var scaleFactor = modelImportSettings.ScaleFactor;
		EditorUIUtility.DrawLabeledField("Scale Factor", () => ImGui.InputFloat("##value", ref scaleFactor));

		// Reimporting a model is expensive, so the edit is committed once the field loses focus
		// rather than on every keystroke.
		if (ImGui.IsItemDeactivatedAfterEdit() == false)
		{
			return;
		}

		if (float.IsFinite(scaleFactor) == false || scaleFactor <= 0.0f)
		{
			return;
		}

		if (scaleFactor == modelImportSettings.ScaleFactor)
		{
			return;
		}

		modelImportSettings.ScaleFactor = scaleFactor;
		metadata.SetImportSettings(modelImportSettings);
		SaveModelMetadata(asset, metadata);
	}

	private AssetSourceMetaFile? EnsureMetadataLoaded(AssetDatabaseEntry asset)
	{
		if (_loadedModelAssetId == asset.Id && _loadedMetadata is not null)
		{
			return _loadedMetadata;
		}

		try
		{
			_loadedModelAssetId = asset.Id;
			_loadedMetadata = _metadataStore.Load(_projectService.GetAbsolutePath(asset.RelativeMetaPath));
			return _loadedMetadata;
		}
		catch
		{
			_loadedModelAssetId = asset.Id;
			_loadedMetadata = null;
			return null;
		}
	}

	private void SaveModelMetadata(AssetDatabaseEntry asset, AssetSourceMetaFile metadata)
	{
		_metadataStore.Save(_projectService.GetAbsolutePath(asset.RelativeMetaPath), metadata);
		_loadedMetadata = metadata;
		_loadedModelAssetId = asset.Id;
		_projectService.RefreshAssetSource(asset.RelativeSourcePath);
	}
}
