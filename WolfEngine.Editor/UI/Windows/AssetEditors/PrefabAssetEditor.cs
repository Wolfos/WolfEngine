using ImGuiNET;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.UI;

public sealed class PrefabAssetEditor
{
	public void Draw(AssetDatabaseEntry asset)
	{
		if (asset.TryGetSummary<PrefabAssetSummary>(out var summary) == false)
		{
			ImGui.TextUnformatted("Prefab");
			return;
		}

		ImGui.TextUnformatted($"Root Entity: {summary.RootEntityId}");
		ImGui.TextUnformatted($"Entities: {summary.EntityCount}");
	}
}
