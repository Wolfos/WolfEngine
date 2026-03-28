namespace WolfEngine.Editor.UI;

public interface IAssetSelectionService
{
	Guid? SelectedAssetId { get; }
	void Select(Guid assetId, bool requestFocus = true);
	void Clear();
	bool ConsumeFocusRequest();
}

public sealed class AssetSelectionService : IAssetSelectionService
{
	public Guid? SelectedAssetId { get; private set; }
	private bool _focusRequested;

	public void Select(Guid assetId, bool requestFocus = true)
	{
		SelectedAssetId = assetId;
		_focusRequested = requestFocus;
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
