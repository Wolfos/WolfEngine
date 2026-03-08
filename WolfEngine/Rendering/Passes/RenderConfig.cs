using WolfEngine.AssetPipeline;

namespace WolfEngine.Rendering.Passes;

public class RenderConfig: IDataAsset
{
	public SkyboxPass.Config SkyboxConfig { get; set; } = new();
}