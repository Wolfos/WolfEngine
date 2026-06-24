using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.UI;

public interface IAssetThumbnailLoader
{
	bool TryGetTextureThumbnailId(AssetDatabaseEntry asset, out nint textureId);
}
