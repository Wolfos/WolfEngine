using System;
using ImGuiNET;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.UI;

public sealed class SceneAssetEditor
{
	public void Draw(AssetDatabaseEntry asset)
	{
		if (asset.TryGetSummary<SceneAssetSummary>(out var summary) == false)
		{
			ImGui.TextUnformatted("Scene summary unavailable.");
			return;
		}

		ImGui.TextUnformatted("Editor Scene");
		ImGui.Separator();
		ImGui.TextUnformatted($"Global Cell: {summary.GlobalCellId}");
		ImGui.TextUnformatted($"Spatial Cells: {summary.SpatialCellCount}");
	}
}
