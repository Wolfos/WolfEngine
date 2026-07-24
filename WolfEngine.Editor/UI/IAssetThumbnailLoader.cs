using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.UI;

public enum AssetThumbnailState
{
	Unavailable,
	Loading,
	Ready
}

public interface IAssetThumbnailLoader
{
	AssetThumbnailState GetTextureThumbnailState(AssetDatabaseEntry asset, out nint textureId);
	bool TryGetTextureThumbnailId(AssetDatabaseEntry asset, out nint textureId);
}
