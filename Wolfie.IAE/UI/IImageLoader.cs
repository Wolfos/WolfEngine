namespace Wolfie.IAE.UI;

public interface IImageLoader
{
	bool TryGetImGuiTextureId(string path, out nint textureId, bool isSrgb = false);
	nint GetImGuiTextureId(string path, bool isSrgb = false);
}
