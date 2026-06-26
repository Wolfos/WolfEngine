using System.Numerics;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Rendering.Passes;

[RuntimeAsset(AssetType.DataAsset, typeof(RenderConfig), typeof(IDataAssetRuntimeResolver))]
public class RenderConfig: IDataAsset
{
	public AmbientOcclusionConfig AmbientOcclusion { get; set; } = new();
	public DiffuseGlobalIlluminationConfig DiffuseGlobalIllumination { get; set; } = new();
	public ShadowMapConfig ShadowMaps { get; set; } = new();
	public SkyboxPass.Config SkyboxConfig { get; set; } = new();
	public TemporalAntiAliasingConfig TemporalAntiAliasing { get; set; } = new();
	public TonemappingConfig Tonemapping { get; set; } = new();
	public DecalConfig Decals { get; set; } = new();
}

public struct ShadowMapConfig
{
	public ShadowMapConfig()
	{
	}

	public int CascadeCount { get; set; } = ShadowMapPass.MaxCascadeCount;
	public int CascadeResolution { get; set; } = ShadowMapPass.DefaultCascadeResolution;
	public float CascadeBlendDistance { get; set; } = ShadowMapPass.DefaultCascadeBlendDistance;
	public float MaxDistance { get; set; } = ShadowMapPass.DefaultMaxShadowDistance;
	public float DepthBias { get; set; } = ShadowMapPass.DefaultDepthBias;
}

public enum DiffuseGlobalIlluminationMode
{
	None,
	RayTracedDdgi
}

public struct DdgiProbeCounts
{
	public DdgiProbeCounts()
	{
	}

	public int X { get; set; } = 16;
	public int Y { get; set; } = 8;
	public int Z { get; set; } = 16;
}

public struct DiffuseGlobalIlluminationConfig
{
	public DiffuseGlobalIlluminationConfig()
	{
	}

	public bool Enabled { get; set; } = false;
	public DiffuseGlobalIlluminationMode Mode { get; set; } = DiffuseGlobalIlluminationMode.RayTracedDdgi;
	public Vector3 Origin { get; set; } = Vector3.Zero;
	public DdgiProbeCounts ProbeCounts { get; set; } = new();
	public float ProbeSpacing { get; set; } = 2.0f;
	public int RaysPerProbe { get; set; } = 64;
	public int ProbeUpdateFrames { get; set; } = 8;
	public float MaxRayDistance { get; set; } = 6.0f;
	public float NormalBias { get; set; } = 0.05f;
	public float ViewBias { get; set; } = 0.2f;
	public float HorizontalBlendDistance { get; set; } = 6.0f;
	public float VerticalBlendDistance { get; set; } = 6.0f;
	public float IrradianceTemporalBlendSpeed { get; set; } = 0.08f;
	public float Hysteresis { get; set; } = 0.95f;
	public bool ProbeRelocationEnabled { get; set; } = true;
	public float ProbeMinFrontfaceDistance { get; set; } = 0.2f;
	public float ProbeBackfaceThreshold { get; set; } = 0.25f;
	public float ProbeMaxRelocationDistanceFactor { get; set; } = 0.45f;
	public bool DebugProbeSpheres { get; set; }
	public float DebugProbeSphereRadius { get; set; } = 0.15f;
}

public enum AmbientOcclusionMode
{
	VisibilityBitmask,
	RayTraced
}

public enum AmbientOcclusionResolution
{
	Full,
	Half
}

public struct AmbientOcclusionConfig
{
	public AmbientOcclusionConfig()
	{
	}

	public bool Enabled { get; set; } = true;
	public AmbientOcclusionMode Mode { get; set; } = AmbientOcclusionMode.VisibilityBitmask;
	public AmbientOcclusionResolution Resolution { get; set; } = AmbientOcclusionResolution.Full;
	public VisibilityBitmaskAmbientOcclusionSettings VisibilityBitmaskSettings { get; set; } = new();
	public RayTracedAmbientOcclusionSettings RayTracedSettings { get; set; } = new();
	public float BlurSharpness { get; set; } = 16.0f;
}

public struct VisibilityBitmaskAmbientOcclusionSettings
{
	public VisibilityBitmaskAmbientOcclusionSettings()
	{
	}

	public int SliceCount { get; set; } = 2;
	public int StepCount { get; set; } = 4;
	public float Radius { get; set; } = 0.4f;
	public float Thickness { get; set; } = 0.3f;
	public float Bias { get; set; } = 0.03f;
	public float Strength { get; set; } = 0.6f;
	public float Power { get; set; } = 1.5f;
}

public struct RayTracedAmbientOcclusionSettings
{
	public RayTracedAmbientOcclusionSettings()
	{
	}

	public float Radius { get; set; } = 2.0f;
	public float Bias { get; set; } = 0.03f;
	public float Strength { get; set; } = 1.0f;
}

public struct DecalConfig
{
	public DecalConfig()
	{
	}

	public bool Enabled { get; set; } = true;
	public int MaxProjectorCount { get; set; } = 256;
	public bool DebugProjectorBounds { get; set; }
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

	public bool Enabled { get; set; } = true;
	public int PhaseCount { get; set; } = 32;
	public float OpaqueDepthThreshold { get; set; } = 0.004f;
	public float AlphaTestDepthThreshold { get; set; } = 0.012f;
	public float OpaqueClampSigma { get; set; } = 1.25f;
	public float AlphaTestClampSigma { get; set; } = 0.85f;
	public float LowMotionClampExpansion { get; set; } = 1.35f;
	public float HighMotionClampExpansion { get; set; } = 1.1f;
	public float ClampExpansionMotionScale { get; set; } = 0.5f;
	public float LowMotionOpaqueHistoryWeight { get; set; } = 0.975f;
	public float HighMotionOpaqueHistoryWeight { get; set; } = 0.85f;
	public float OpaqueHistoryMotionScale { get; set; } = 0.35f;
	public float LowMotionAlphaTestHistoryWeight { get; set; } = 0.9f;
	public float HighMotionAlphaTestHistoryWeight { get; set; } = 0.7f;
	public float AlphaTestHistoryMotionScale { get; set; } = 0.45f;
	public bool EnableCasSharpen { get; set; } = true;
	public float CasSharpness { get; set; } = 1.0f;
}
