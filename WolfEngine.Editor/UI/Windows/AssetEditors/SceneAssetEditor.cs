using ImGuiNET;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.UI;

public sealed class SceneAssetEditor
{
	public void Draw(AssetDatabaseEntry asset)
	{
		if (asset.SceneSummary is null)
		{
			ImGui.TextUnformatted("Scene summary unavailable.");
			return;
		}

		ImGui.TextUnformatted("Editor Scene");
		ImGui.Separator();
		ImGui.TextUnformatted($"Global Cell: {asset.SceneSummary.GlobalCellPath}");
		ImGui.TextUnformatted($"Spatial Cells: {asset.SceneSummary.SpatialCellCount}");
	}
}
