using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public interface ITextureAssetStore
{
	TextureImportSettings CreateDefaultImportSettings();
}

public sealed class TextureAssetStore : ITextureAssetStore
{
	public TextureImportSettings CreateDefaultImportSettings()
	{
		return new TextureImportSettings();
	}
}
