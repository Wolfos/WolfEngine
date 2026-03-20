namespace WolfEngine.Editor.UI;

public interface IAssetSelectionService
{
	Guid? SelectedAssetId { get; }
	void Select(Guid assetId);
	void Clear();
	bool ConsumeFocusRequest();
}

public sealed class AssetSelectionService : IAssetSelectionService
{
	public Guid? SelectedAssetId { get; private set; }
	private bool _focusRequested;

	public void Select(Guid assetId)
	{
		SelectedAssetId = assetId;
		_focusRequested = true;
	}

	public void Clear()
	{
		SelectedAssetId = null;
		_focusRequested = false;
	}

	public bool ConsumeFocusRequest()
	{
		if (_focusRequested == false)
		{
			return false;
		}

		_focusRequested = false;
		return true;
	}
}
