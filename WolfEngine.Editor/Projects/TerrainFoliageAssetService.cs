using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

internal sealed record CreatedTerrainFoliageAsset(Guid Id, string RelativePath, AssetSourceMetaFile Metadata);

public sealed class TerrainFoliageAssetService
{
	private readonly IEditorProjectService _projectService;
	private readonly IDataAssetCreator _creator;
	private readonly IAssetMetadataStore _metadata;
	private readonly IEditorAssetSnapshotService _assetSnapshots;
	private readonly Dictionary<(string ProjectRoot, Guid AssetId), TerrainFoliage> _dirtyAssets = new();

	public TerrainFoliageAssetService(
		IEditorProjectService projectService,
		IDataAssetCreator creator,
		IAssetMetadataStore metadata,
		IEditorAssetSnapshotService assetSnapshots)
	{
		_projectService = projectService;
		_creator = creator;
		_metadata = metadata;
		_assetSnapshots = assetSnapshots;
	}

	internal bool CanEdit(Guid assetId) =>
		_projectService.TryGetAsset(assetId, out _) && _projectService.IsAssetReadOnly(assetId) == false;

	internal CreatedTerrainFoliageAsset Create(Guid terrainAssetId)
	{
		if (_projectService.TryGetAsset(terrainAssetId, out var terrainAsset) == false ||
		    _projectService.IsAssetReadOnly(terrainAssetId))
		{
			throw new InvalidOperationException("The terrain asset needs a writable project folder for its foliage asset.");
		}

		var folder = Path.GetDirectoryName(terrainAsset.RelativeSourcePath) ?? "Assets";
		var name = Path.GetFileNameWithoutExtension(terrainAsset.RelativeSourcePath);
		var result = _creator.CreateDataAsset(typeof(TerrainFoliage), folder, $"{name} Foliage");
		if (result.Success == false || result.AssetId is not { } id || _projectService.TryGetAsset(id, out var asset) == false)
		{
			throw new InvalidOperationException(result.ErrorMessage ?? "The created foliage asset was not found.");
		}

		var path = _projectService.GetAbsoluteAssetPath(id, asset.RelativeSourcePath);
		var metadata = _metadata.Load(AssetFileExtensions.GetMetaPath(path));
		return new CreatedTerrainFoliageAsset(id, asset.RelativeSourcePath, metadata);
	}

	internal void Delete(CreatedTerrainFoliageAsset created)
	{
		if (_projectService.TryGetAsset(created.Id, out var asset))
		{
			_projectService.DeleteAssetSource(asset.RelativeSourcePath);
		}

		_dirtyAssets.Remove(GetKey(created.Id));
	}

	internal void Restore(CreatedTerrainFoliageAsset created)
	{
		var result = _creator.RestoreDataAsset(typeof(TerrainFoliage), created.RelativePath, created.Metadata);
		if (result.Success == false || result.AssetId != created.Id)
		{
			throw new InvalidOperationException(result.ErrorMessage ?? "The foliage asset could not be restored.");
		}
	}

	internal TerrainFoliage? GetPending(Guid assetId) => _dirtyAssets.GetValueOrDefault(GetKey(assetId));

	internal void MarkDirty(Guid assetId, TerrainFoliage foliage)
	{
		_dirtyAssets[GetKey(assetId)] = foliage;
	}

	internal void SaveDirtyAssets()
	{
		var projectRoot = _projectService.ProjectRootPath;
		foreach (var pair in _dirtyAssets.Where(pair => pair.Key.ProjectRoot == projectRoot).ToArray())
		{
			if (_projectService.TryGetAsset(pair.Key.AssetId, out var asset))
			{
				_assetSnapshots.SaveDataAsset(asset, typeof(TerrainFoliage), pair.Value);
			}

			_dirtyAssets.Remove(pair.Key);
		}
	}

	private (string ProjectRoot, Guid AssetId) GetKey(Guid assetId) => (_projectService.ProjectRootPath ?? string.Empty, assetId);
}
