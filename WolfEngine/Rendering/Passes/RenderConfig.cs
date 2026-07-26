using System.Numerics;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Rendering.Passes;

[RuntimeAsset(AssetType.DataAsset, typeof(RenderConfig), typeof(IDataAssetRuntimeResolver))]
public class RenderConfig: IDataAsset
{
	public AmbientOcclusionConfig AmbientOcclusion { get; set; } = new();
	public ReflectionConfig Reflections { get; set; } = new();
	public DiffuseGlobalIlluminationConfig DiffuseGlobalIllumination { get; set; } = new();
	public ShadowMapConfig ShadowMaps { get; set; } = new();
	public SkyboxPass.Config SkyboxConfig { get; set; } = new();
	public TemporalAntiAliasingConfig TemporalAntiAliasing { get; set; } = new();
	public TonemappingConfig Tonemapping { get; set; } = new();
	public BloomConfig Bloom { get; set; } = new();
	public DecalConfig Decals { get; set; } = new();
}

public enum ReflectionMode
{
	ScreenSpace,
	RayTraced
}

public struct ReflectionConfig
{
	public ReflectionConfig()
	{
	}

	public bool Enabled { get; set; } = true;
	public ReflectionMode Mode { get; set; } = ReflectionMode.ScreenSpace;

	/// <summary>
	/// How strongly a screen-space hit is reprojected through its motion vector before the
	/// previous frame's color pyramid is sampled, in 0..1. One is geometrically correct per
	/// surface point; zero samples the previous frame where the hit is now, trading a frame of
	/// lag for a fetch that does not warp per pixel as the camera moves.
	/// </summary>
	public float ReprojectionStrength { get; set; } = 1.0f;
	public ScreenSpaceReflectionSettings ScreenSpaceSettings { get; set; } = new();
	public RayTracedReflectionSettings RayTracedSettings { get; set; } = new();
}

public struct ScreenSpaceReflectionSettings
{
	public ScreenSpaceReflectionSettings()
	{
	}

	public int MaxSteps { get; set; } = 48;
	public int BinarySearchSteps { get; set; } = 5;
	public float MaxRayDistance { get; set; } = 40.0f;
	public float Thickness { get; set; } = 0.15f;
	public float Bias { get; set; } = 0.03f;
	public float MaxRoughness { get; set; } = 0.65f;
	public float EdgeFade { get; set; } = 0.08f;
	public float Intensity { get; set; } = 1.0f;
}

public struct RayTracedReflectionSettings
{
	public RayTracedReflectionSettings()
	{
	}

	public float MaxRayDistance { get; set; } = 100.0f;
	public float Bias { get; set; } = 0.03f;
	public float MaxRoughness { get; set; } = 0.8f;
	public float ScreenReuseThickness { get; set; } = 0.2f;

	/// <summary>
	/// Fraction of the screen-reuse depth budget over which reused screen color cross-fades into
	/// freshly shaded hit material, in 0..1. Zero restores a hard cutoff, which makes the choice
	/// binary per pixel and tends to crawl as the camera moves.
	/// </summary>
	public float ScreenReuseFalloff { get; set; } = 0.5f;
	public float Intensity { get; set; } = 1.0f;
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
	public float RecursiveBounceEnergy { get; set; } = DdgiUtilities.DefaultRecursiveBounceEnergy;
	public bool ProbeRelocationEnabled { get; set; } = true;
	public float ProbeMinFrontfaceDistance { get; set; } = 0.2f;
	public float ProbeBackfaceThreshold { get; set; } = 0.25f;
	public float ProbeMaxRelocationDistanceFactor { get; set; } = 0.45f;
	public bool DebugProbeSpheres { get; set; }
	public float DebugProbeSphereRadius { get; set; } = 0.15f;
	public bool DebugProbeClassificationStats { get; set; }
	public bool DebugFirstProbeRelocationReadback { get; set; }
	public int DebugProbeRelocationReadbackIndex { get; set; }
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

public enum BloomQuality
{
	Low,
	Medium,
	High
}

public struct BloomConfig
{
	public BloomConfig()
	{
	}

	public bool Enabled { get; set; } = true;
	public float Threshold { get; set; } = 1.0f;
	public float SoftKnee { get; set; } = 0.5f;
	public float Intensity { get; set; } = 0.08f;
	public float Scatter { get; set; } = 0.7f;
	public Vector3 Tint { get; set; } = Vector3.One;
	public BloomQuality Quality { get; set; } = BloomQuality.High;
}

public struct TemporalAntiAliasingConfig
{
	public TemporalAntiAliasingConfig()
	{
	}

	public bool Enabled { get; set; } = true;
	public int PhaseCount { get; set; } = 8;
	public float StaticHistoryWeight { get; set; } = 0.95f;
	public float MovingHistoryWeight { get; set; } = 0.65f;
	public float MotionResponsePixels { get; set; } = 8.0f;
	public float DepthRejectionAbsolute { get; set; } = 0.02f;
	public float DepthRejectionRelative { get; set; } = 0.01f;
	public float VarianceClipGamma { get; set; } = 1.0f;
	public float AlphaTestHistoryScale { get; set; } = 0.75f;
	public bool EnableCasSharpen { get; set; } = true;
	public float CasSharpness { get; set; } = 0.35f;
}
