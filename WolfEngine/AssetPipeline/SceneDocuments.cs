namespace WolfEngine;

/// <summary>
/// A scene's manifest as the editor saves it in a <c>.scene.json</c> file: the cells that make up the scene.
/// It is small and read once per scene load, so it ships as JSON.
/// </summary>
public sealed class SceneManifestDocument
{
	public const int CurrentVersion = 1;

	public int Version { get; set; } = CurrentVersion;
	public Guid GlobalCellId { get; set; }
	public List<SceneSpatialCellDocument> SpatialCells { get; set; } = [];
}

public sealed class SceneSpatialCellDocument
{
	public Guid CellId { get; set; }
}

/// <summary>
/// A scene cell as the editor saves it in a <c>.cell.json</c> file. The build cooks it to the binary format
/// <see cref="AssetPipeline.CookedSceneCell"/> reads.
/// </summary>
public sealed class SceneCellDocument
{
	public const int CurrentVersion = 1;

	public int Version { get; set; } = CurrentVersion;
	public List<EntityDocument> Entities { get; set; } = [];
}
