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
	public int PhaseCount { get; set; } = 16;
	public float DepthThresholdOpaque { get; set; } = 0.01f;
	public float DepthThresholdAlphaTest { get; set; } = 0.03f;
	public float ClampSigmaOpaque { get; set; } = 1.25f;
	public float ClampSigmaAlphaTest { get; set; } = 0.85f;
	public float ClampExpansionLowMotion { get; set; } = 1.35f;
	public float ClampExpansionHighMotion { get; set; } = 1.1f;
	public float ClampExpansionMotionScale { get; set; } = 0.5f;
	public float HistoryWeightOpaqueLowMotion { get; set; } = 0.9925f;
	public float HistoryWeightOpaqueHighMotion { get; set; } = 0.9f;
	public float HistoryWeightOpaqueMotionScale { get; set; } = 0.1f;
	public float HistoryWeightAlphaTestLowMotion { get; set; } = 0.95f;
	public float HistoryWeightAlphaTestHighMotion { get; set; } = 0.75f;
	public float HistoryWeightAlphaTestMotionScale { get; set; } = 0.15f;
}
