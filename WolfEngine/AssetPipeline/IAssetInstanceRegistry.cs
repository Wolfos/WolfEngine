namespace WolfEngine.AssetPipeline;

public interface IAssetInstanceRegistry
{
	object GetInstance(Guid assetId, Type expectedType);
	void RefreshProject(string projectRootPath, AssetDatabase database);
	void Clear();
}
