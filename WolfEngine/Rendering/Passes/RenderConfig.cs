using WolfEngine.AssetPipeline;

namespace WolfEngine.Rendering.Passes;

[RuntimeAsset(AssetType.DataAsset, typeof(RenderConfig), typeof(IDataAssetRuntimeResolver))]
public class RenderConfig: IDataAsset
{
	public SkyboxPass.Config SkyboxConfig { get; set; } = new();
}
