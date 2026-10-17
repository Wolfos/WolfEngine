using ImGuiNET;
using System.Numerics;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

public sealed class AssetEditorWindow : EditorWindow
{
	private readonly IEditorProjectService _projectService;
	private readonly IAssetSelectionService _assetSelectionService;
	private readonly IEditorAssetHandlerRegistry _assetHandlerRegistry;
	private readonly MaterialAssetEditor _materialAssetEditor;

	public AssetEditorWindow(
		IEditorProjectService projectService,
		IAssetSelectionService assetSelectionService,
		IEditorAssetHandlerRegistry assetHandlerRegistry,
		MaterialAssetEditor materialAssetEditor)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_assetSelectionService = assetSelectionService ?? throw new ArgumentNullException(nameof(assetSelectionService));
		_assetHandlerRegistry = assetHandlerRegistry ?? throw new ArgumentNullException(nameof(assetHandlerRegistry));
		_materialAssetEditor = materialAssetEditor ?? throw new ArgumentNullException(nameof(materialAssetEditor));
	}

	public override string Name => "Asset Editor";
	public override void OnHidden() => _materialAssetEditor.HidePreview();

	public override void Draw(EditorScene scene)
	{
		ImGui.SetNextWindowPos(new Vector2(860.0f, 420.0f), ImGuiCond.FirstUseEver);
		ImGui.SetNextWindowSize(new Vector2(480.0f, 600.0f), ImGuiCond.FirstUseEver);
		if (_assetSelectionService.SelectedAssetId.HasValue && _assetSelectionService.ConsumeFocusRequest())
		{
			ImGui.SetNextWindowFocus();
		}

		Begin();
		var hostVisible = !ImGui.IsWindowDocked() || IsSelectedTab;
		_materialAssetEditor.SetHostVisible(hostVisible);
		if (!hostVisible)
		{
			ImGui.End();
			return;
		}

		if (_projectService.HasOpenProject == false)
		{
			_materialAssetEditor.HidePreview();
			ImGui.TextUnformatted("No project open.");
			ImGui.End();
			return;
		}

		var selectedAssetId = _assetSelectionService.SelectedAssetId;
		if (selectedAssetId.HasValue == false)
		{
			_materialAssetEditor.HidePreview();
			ImGui.TextUnformatted("Select an asset in the Assets window to edit it.");
			ImGui.End();
			return;
		}

		if (_projectService.TryGetAsset(selectedAssetId.Value, out var asset) == false)
		{
			_materialAssetEditor.HidePreview();
			ImGui.TextUnformatted("Selected asset no longer exists in the current project.");
			ImGui.End();
			return;
		}

		ImGui.TextUnformatted(asset.Name);
		ImGui.TextDisabled(asset.RelativeAssetPath);
		var readOnly = _projectService.IsAssetReadOnly(asset.Id);
		if (readOnly)
			ImGui.TextDisabled("Read-only mounted asset");
		ImGui.Separator();
		if (asset.Type != AssetType.Material) _materialAssetEditor.HidePreview();
		_materialAssetEditor.SetReadOnly(readOnly);

		if (_assetHandlerRegistry.TryGetHandler(asset.Type, out var handler))
		{
			if (readOnly && asset.Type != AssetType.Material) ImGui.BeginDisabled();
			handler.DrawEditor(asset);
			if (readOnly && asset.Type != AssetType.Material) ImGui.EndDisabled();
		}
		else
		{
			_materialAssetEditor.HidePreview();
			ImGui.TextUnformatted($"No editor available for asset type '{asset.Type}'.");
		}

		ImGui.End();
	}
}
