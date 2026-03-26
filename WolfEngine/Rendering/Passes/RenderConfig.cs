using WolfEngine.AssetPipeline;

namespace WolfEngine.Rendering.Passes;

[RuntimeAsset(AssetType.DataAsset, typeof(RenderConfig), typeof(IDataAssetRuntimeResolver))]
public class RenderConfig: IDataAsset
{
	public VBAOPass.Config VBAOConfig { get; set; } = new();
	public SkyboxPass.Config SkyboxConfig { get; set; } = new();
	public TemporalAntiAliasingConfig TemporalAntiAliasing { get; set; } = new();
	public TonemappingConfig Tonemapping { get; set; } = new();
}

public struct TonemappingConfig
{
	public TonemappingConfig()
	{
	}

	public float Exposure { get; set; } = 1.0f;
}

public struct TemporalAntiAliasingConfig
{
	public TemporalAntiAliasingConfig()
	{
	}

	// Temporary fallback until the DirectSR path replaces this resolve.
	public bool Enabled { get; set; } = true;
	public int PhaseCount { get; set; } = 32;
	public float OpaqueDepthThreshold { get; set; } = 0.01f;
	public float AlphaTestDepthThreshold { get; set; } = 0.03f;
	public float OpaqueClampSigma { get; set; } = 1.25f;
	public float AlphaTestClampSigma { get; set; } = 0.85f;
	public float LowMotionClampExpansion { get; set; } = 1.35f;
	public float HighMotionClampExpansion { get; set; } = 1.1f;
	public float ClampExpansionMotionScale { get; set; } = 0.5f;
	public float LowMotionOpaqueHistoryWeight { get; set; } = 0.9925f;
	public float HighMotionOpaqueHistoryWeight { get; set; } = 0.9f;
	public float OpaqueHistoryMotionScale { get; set; } = 0.1f;
	public float LowMotionAlphaTestHistoryWeight { get; set; } = 0.95f;
	public float HighMotionAlphaTestHistoryWeight { get; set; } = 0.75f;
	public float AlphaTestHistoryMotionScale { get; set; } = 0.15f;
	public bool EnableCasSharpen { get; set; } = true;
	public float CasSharpness { get; set; } = 1.0f;
}
