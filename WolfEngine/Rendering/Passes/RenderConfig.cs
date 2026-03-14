using WolfEngine.AssetPipeline;

namespace WolfEngine.Rendering.Passes;

[RuntimeAsset(AssetType.DataAsset, typeof(RenderConfig), typeof(IDataAssetRuntimeResolver))]
public class RenderConfig: IDataAsset
{
	public VBAOPass.Config VBAOConfig { get; set; } = new();
	public SkyboxPass.Config SkyboxConfig { get; set; } = new();
	public TemporalAntiAliasingConfig TemporalAntiAliasing { get; set; } = new();
}

public struct TemporalAntiAliasingConfig
{
	public TemporalAntiAliasingConfig()
	{
	}

	public bool Enabled { get; set; } = true;
}
