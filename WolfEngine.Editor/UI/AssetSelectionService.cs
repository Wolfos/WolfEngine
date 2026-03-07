namespace WolfEngine.Editor.UI;

public interface IAssetSelectionService
{
	Guid? SelectedAssetId { get; }
	void Select(Guid assetId);
	void Clear();
}

public sealed class AssetSelectionService : IAssetSelectionService
{
	public Guid? SelectedAssetId { get; private set; }

	public void Select(Guid assetId)
	{
		SelectedAssetId = assetId;
	}

	public void Clear()
	{
		SelectedAssetId = null;
	}
}
