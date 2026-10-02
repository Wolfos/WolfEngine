namespace WolfEngine.AssetPipeline;

public interface IDataAsset
{
	
}

/// <summary>Persistent asset IDs used by a data asset's imported runtime state.</summary>
public interface IDataAssetDependencies
{
	IEnumerable<Guid> GetDependencies();
}
