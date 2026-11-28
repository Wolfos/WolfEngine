using WolfEngine.ECS;
using WolfEngine.Mathematics;

namespace WolfEngine.Editor;

public class EditorScene
{
	public Guid AssetId { get; set; }
	public string Name { get; set; } = "Untitled Scene";
	public string RelativeAssetPath { get; set; } = string.Empty;
	public Guid GlobalCellId { get; set; }
	public World World { get; set; } = new(WorldTag.Authoring);
	public Dictionary<Entity, string> EntityIcons { get; set; } = new();
	public Cell GlobalCell { get; set; } = new();
	public Dictionary<Int2, Cell> SpatialCells { get; set; } = new();
	public Dictionary<Int2, Guid> SpatialCellIds { get; set; } = new();
	public Dictionary<Entity, SceneCellKey> EntityCellKeys { get; set; } = new();
	public Dictionary<Entity, Guid> EntityIds { get; set; } = new();
	public Dictionary<Entity, List<SavedPrefabLink>> EntityPrefabSourcePaths { get; set; } = new();

	/// <summary>Finds the live entity with this persistent ID.</summary>
	public bool TryGetEntity(Guid entityId, out Entity entity)
	{
		if (entityId != Guid.Empty)
		{
			foreach (var entry in EntityIds)
			{
				if (entry.Value == entityId && World.IsAlive(entry.Key))
				{
					entity = entry.Key;
					return true;
				}
			}
		}

		entity = default;
		return false;
	}
}
