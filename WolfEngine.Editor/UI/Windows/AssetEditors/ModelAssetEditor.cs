using ImGuiNET;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

public sealed class ModelAssetEditor
{
	private readonly IEditorProjectService _projectService;
	private readonly IAssetMetadataStore _metadataStore;
	private readonly IPropertyDrawerRegistry _propertyDrawerRegistry;
	private readonly IIconManager _icons;
	private readonly IAssetSelectionService _assetSelectionService;
	private AssetSourceMetaFile? _loadedMetadata;
	private Guid? _loadedModelAssetId;
    private string _ignoredChannelText = "";

	public ModelAssetEditor(
		IEditorProjectService projectService,
		IAssetMetadataStore metadataStore,
		IPropertyDrawerRegistry propertyDrawerRegistry,
		IIconManager icons,
		IAssetSelectionService assetSelectionService)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_metadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
		_propertyDrawerRegistry = propertyDrawerRegistry ?? throw new ArgumentNullException(nameof(propertyDrawerRegistry));
		_icons = icons ?? throw new ArgumentNullException(nameof(icons));
		_assetSelectionService = assetSelectionService ?? throw new ArgumentNullException(nameof(assetSelectionService));
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
		var rig = new AssetRef<Skeleton>
		{
			NodeId = modelImportSettings.AnimationSkeletonId
		};
		var rigSelectionButton = new AssetLinkSelectionButton(
			_icons.Get("search"),
			assetId => _assetSelectionService.Select(assetId));
        if (_propertyDrawerRegistry.DrawAssetRef("Animation rig", ref rig, rigSelectionButton))
        {
            modelImportSettings.AnimationSkeletonId = rig.NodeId;
            metadata.SetImportSettings(modelImportSettings);
            SaveModelMetadata(asset, metadata);
        }
        ImGui.InputText("Ignored channels (; separated)", ref _ignoredChannelText, 1024);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            modelImportSettings.IgnoredAnimationChannels = _ignoredChannelText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            metadata.SetImportSettings(modelImportSettings); SaveModelMetadata(asset, metadata);
        }
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
			_loadedMetadata = _metadataStore.Load(
				_projectService.GetAbsoluteAssetPath(asset.Id, asset.RelativeMetaPath));
            _ignoredChannelText = string.Join(";", _loadedMetadata.GetImportSettingsOrDefault(() => new ModelImportSettings()).IgnoredAnimationChannels);
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
		if (_projectService.IsAssetReadOnly(asset.Id))
			throw new InvalidOperationException($"Asset '{asset.Id}' belongs to a read-only mount.");
		_metadataStore.Save(_projectService.GetAbsoluteAssetPath(asset.Id, asset.RelativeMetaPath), metadata);
		_loadedMetadata = metadata;
		_loadedModelAssetId = asset.Id;
		_projectService.RefreshAssetSource(asset.RelativeSourcePath);
	}
}
