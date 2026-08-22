using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

public sealed class TerrainAssetEditor
{
	private static readonly int[] ResolutionOptions = [128, 256, 512, 1024, 2048, 4096, 8192];

	private readonly ITerrainAssetPersistenceService _terrainAssetPersistenceService;
	private readonly IEditorUndoRedoService _undoRedoService;
	private readonly IEditorInteractionState _interactionState;

	public TerrainAssetEditor(
		ITerrainAssetPersistenceService terrainAssetPersistenceService,
		IEditorUndoRedoService undoRedoService,
		IEditorInteractionState interactionState)
	{
		_terrainAssetPersistenceService = terrainAssetPersistenceService ?? throw new ArgumentNullException(nameof(terrainAssetPersistenceService));
		_undoRedoService = undoRedoService ?? throw new ArgumentNullException(nameof(undoRedoService));
		_interactionState = interactionState ?? throw new ArgumentNullException(nameof(interactionState));
	}

	public void Draw(AssetDatabaseEntry asset)
	{
		var terrainAsset = AssetDatabase.GetInstance<TerrainAsset>(asset.Id);
		if (terrainAsset is null)
		{
			ImGui.TextUnformatted("Terrain asset is unavailable.");
			return;
		}

		DrawResolutionCombo("Heightmap Resolution", terrainAsset.HeightmapWidth, resolution => Resize(asset.Id, terrainAsset, resolution, terrainAsset.LayerMapWidth));
		DrawResolutionCombo("Splatmap Resolution", terrainAsset.LayerMapWidth, resolution => Resize(asset.Id, terrainAsset, terrainAsset.HeightmapWidth, resolution));
		ImGui.TextDisabled("Changing a resolution resamples existing terrain data smoothly.");
	}

	private static void DrawResolutionCombo(string label, int currentResolution, Action<int> onSelected)
	{
		var preview = $"{currentResolution} x {currentResolution}";
		EditorUIUtility.Combo(label, preview, () =>
		{
			for (var i = 0; i < ResolutionOptions.Length; i++)
			{
				var resolution = ResolutionOptions[i];
				var selected = resolution == currentResolution;
				if (ImGui.Selectable($"{resolution} x {resolution}", selected) && selected == false)
				{
					onSelected(resolution);
				}

				if (selected)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
		});
	}

	private void Resize(Guid assetId, TerrainAsset terrainAsset, int heightmapResolution, int layerMapResolution)
	{
		var before = terrainAsset.CaptureSnapshot(assetId);
		terrainAsset.ResizeMaps(heightmapResolution, layerMapResolution);
		var after = terrainAsset.CaptureSnapshot(assetId);
		_terrainAssetPersistenceService.RecordPendingTerrainAssetState([after]);
		_undoRedoService.BeginCapture("Resize Terrain Textures");
		_undoRedoService.CommitCapture(new TerrainAssetEditUndoRedoEntry("Resize Terrain Textures", [before], [after]));
		_interactionState.MarkSceneDirty();
	}
}
