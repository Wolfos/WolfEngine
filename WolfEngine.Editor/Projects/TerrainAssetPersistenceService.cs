using System;
using System.Collections.Generic;
using System.Linq;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public interface ITerrainAssetPersistenceService
{
	void RecordPendingTerrainAssetState(IReadOnlyList<TerrainAssetSnapshot> snapshots);
	void ApplyTerrainAssetStates(IReadOnlyList<TerrainAssetSnapshot> snapshots);
	void SaveDirtyTerrainAssets();
}

public sealed class TerrainAssetPersistenceService : ITerrainAssetPersistenceService
{
	private readonly IEditorProjectService _projectService;
	private readonly Dictionary<Guid, TerrainAssetSnapshot> _dirtyStates = new();

	public TerrainAssetPersistenceService(IEditorProjectService projectService)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
	}

	public void RecordPendingTerrainAssetState(IReadOnlyList<TerrainAssetSnapshot> snapshots)
	{
		ArgumentNullException.ThrowIfNull(snapshots);
		for (var i = 0; i < snapshots.Count; i++)
		{
			var snapshot = snapshots[i];
			if (snapshot.AssetId == Guid.Empty)
			{
				continue;
			}

			_dirtyStates[snapshot.AssetId] = CloneSnapshot(snapshot);
		}
	}

	public void ApplyTerrainAssetStates(IReadOnlyList<TerrainAssetSnapshot> snapshots)
	{
		ArgumentNullException.ThrowIfNull(snapshots);
		for (var i = 0; i < snapshots.Count; i++)
		{
			var snapshot = CloneSnapshot(snapshots[i]);
			var terrainAsset = AssetDatabase.GetInstance<TerrainAsset>(snapshot.AssetId);
			terrainAsset?.ApplyMaps(snapshot.Heightmap, snapshot.LayerIndexMap, snapshot.LayerWeightMap);
			if (snapshot.AssetId != Guid.Empty)
			{
				_dirtyStates[snapshot.AssetId] = snapshot;
			}
		}
	}

	public void SaveDirtyTerrainAssets()
	{
		if (_dirtyStates.Count == 0)
		{
			return;
		}

		var dirtySnapshots = _dirtyStates.Values.ToArray();
		for (var i = 0; i < dirtySnapshots.Length; i++)
		{
			SaveSnapshot(dirtySnapshots[i]);
		}

		_dirtyStates.Clear();
	}

	private void SaveSnapshot(TerrainAssetSnapshot snapshot)
	{
		if (_projectService.TryGetAsset(snapshot.AssetId, out var asset) == false ||
		    asset.Type != AssetType.Terrain)
		{
			return;
		}

		var terrainAsset = new TerrainAsset(
			snapshot.Name,
			TerrainAsset.CloneTexture(snapshot.Heightmap),
			TerrainAsset.CloneTexture(snapshot.LayerIndexMap),
			TerrainAsset.CloneTexture(snapshot.LayerWeightMap));
		TerrainAssetSerializer.Write(_projectService.GetAbsolutePath(asset.RelativeAssetPath), terrainAsset);
	}

	private static TerrainAssetSnapshot CloneSnapshot(TerrainAssetSnapshot snapshot)
	{
		return new TerrainAssetSnapshot(
			snapshot.AssetId,
			snapshot.Name,
			TerrainAsset.CloneTexture(snapshot.Heightmap),
			TerrainAsset.CloneTexture(snapshot.LayerIndexMap),
			TerrainAsset.CloneTexture(snapshot.LayerWeightMap));
	}
}
