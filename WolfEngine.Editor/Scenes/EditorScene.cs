using System;
using System.Collections.Generic;
using WolfEngine.ECS;
using WolfEngine.Mathematics;

namespace WolfEngine.Editor;

public class EditorScene
{
	public Guid AssetId { get; set; }
	public string Name { get; set; } = "Untitled Scene";
	public string RelativeAssetPath { get; set; } = string.Empty;
	public World World { get; set; } = new(WorldTag.Authoring);
	public Dictionary<Entity, string> EntityIcons { get; set; } = new();
	public Cell GlobalCell { get; set; } = new();
	public Dictionary<Int2, Cell> SpatialCells { get; set; } = new();
	public Dictionary<Entity, SceneCellKey> EntityCellKeys { get; set; } = new();
	public Dictionary<Entity, Guid> EntityIds { get; set; } = new();
}
