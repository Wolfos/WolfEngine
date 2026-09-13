namespace WolfEngine.AssetPipeline;

public interface IAssetInstanceRegistry
{
	object? GetInstance(Guid assetId, Type expectedType);
	void RefreshProject(string projectRootPath, AssetDatabase database);
	void RefreshCatalog(IAssetCatalog catalog)
	{
		ArgumentNullException.ThrowIfNull(catalog);
		var projectMount = catalog.Mounts.FirstOrDefault(mount => string.Equals(mount.Id, "project", StringComparison.Ordinal));
		if (projectMount is null)
			throw new InvalidOperationException("The asset catalog does not contain a project mount.");
		RefreshProject(projectMount.RootPath, projectMount.Database);
	}
	void InvalidateAssets(IEnumerable<Guid> assetIds);
	void ClearCachedInstances();
	void Clear();
}
