using ImGuiNET;
using System.Numerics;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

public sealed class AssetEditorWindow : EditorWindow
{
	private readonly IEditorProjectService _projectService;
	private readonly IAssetSelectionService _assetSelectionService;
	private readonly IEditorAssetHandlerRegistry _assetHandlerRegistry;

	public AssetEditorWindow(
		IEditorProjectService projectService,
		IAssetSelectionService assetSelectionService,
		IEditorAssetHandlerRegistry assetHandlerRegistry)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_assetSelectionService = assetSelectionService ?? throw new ArgumentNullException(nameof(assetSelectionService));
		_assetHandlerRegistry = assetHandlerRegistry ?? throw new ArgumentNullException(nameof(assetHandlerRegistry));
	}

	public override string Name => "Asset Editor";

	public override void Draw(EditorScene scene)
	{
		ImGui.SetNextWindowPos(new Vector2(860.0f, 420.0f), ImGuiCond.FirstUseEver);
		ImGui.SetNextWindowSize(new Vector2(420.0f, 300.0f), ImGuiCond.FirstUseEver);
		if (_assetSelectionService.SelectedAssetId.HasValue && _assetSelectionService.ConsumeFocusRequest())
		{
			ImGui.SetNextWindowFocus();
		}

		Begin();

		if (_projectService.HasOpenProject == false)
		{
			ImGui.TextUnformatted("No project open.");
			ImGui.End();
			return;
		}

		var selectedAssetId = _assetSelectionService.SelectedAssetId;
		if (selectedAssetId.HasValue == false)
		{
			ImGui.TextUnformatted("Select an asset in the Assets window to edit it.");
			ImGui.End();
			return;
		}

		if (_projectService.TryGetAsset(selectedAssetId.Value, out var asset) == false)
		{
			ImGui.TextUnformatted("Selected asset no longer exists in the current project.");
			ImGui.End();
			return;
		}

		ImGui.TextUnformatted(asset.Name);
		ImGui.TextDisabled(asset.RelativeAssetPath);
		ImGui.Separator();

		if (_assetHandlerRegistry.TryGetHandler(asset.Type, out var handler))
		{
			handler.DrawEditor(asset);
		}
		else
		{
			ImGui.TextUnformatted($"No editor available for asset type '{asset.Type}'.");
		}

		ImGui.End();
	}
}
