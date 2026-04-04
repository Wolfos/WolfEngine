using ImGuiNET;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.UI;

public sealed class PrefabAssetEditor
{
	public void Draw(AssetDatabaseEntry asset)
	{
		if (asset.PrefabSummary is null)
		{
			ImGui.TextUnformatted("Prefab");
			return;
		}

		ImGui.TextUnformatted($"Root Entity: {asset.PrefabSummary.RootEntityId}");
		ImGui.TextUnformatted($"Entities: {asset.PrefabSummary.EntityCount}");
	}
}
