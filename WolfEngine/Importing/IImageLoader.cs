using WolfEngine.Importing;

namespace WolfEngine.Importing;

public interface IImageLoader
{
	ImportedTexture Load(string path, TextureSemantic semantic);
	ImportedTexture LoadEmbedded(byte[] data, TextureSemantic semantic, string nameHint);
}
