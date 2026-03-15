using WolfEngine.Rendering.Abstraction;
using WolfEngine.Mathematics;
using System.Numerics;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// Describes the API-agnostic parameters required to record the deferred lighting compute pass.
/// </summary>
public struct DeferredLightingPassConfig
{
	public required IGfxPipeline Pipeline { get; init; }

	public required DescriptorHandle GBufferAlbedo { get; init; }
	public required DescriptorHandle GBufferNormal { get; init; }
	public required DescriptorHandle GBufferMaterial { get; init; }
	public required DescriptorHandle GBufferEmissive { get; init; }
	public required DescriptorHandle GBufferDepth { get; init; }
	public required DescriptorHandle AmbientOcclusion { get; init; }
	public required DescriptorHandle ShadowMapDepth0 { get; init; }
	public required DescriptorHandle ShadowMapDepth1 { get; init; }
	public required DescriptorHandle ShadowMapDepth2 { get; init; }
	public required DescriptorHandle SkyboxEnvironment { get; init; }
	public required DescriptorHandle SkyboxIrradiance { get; init; }
	public required DescriptorHandle SkyboxPrefilter { get; init; }
	public required DescriptorHandle SkyboxBrdfLut { get; init; }
	public required DescriptorHandle LightingOutput { get; init; }
	public required DescriptorHandle LinearSampler { get; init; }
	public required DescriptorHandle ShadowSampler { get; init; }

	public required IGfxBuffer PointLightBuffer { get; init; }
	public required IGfxBuffer ClusterHeaderBuffer { get; init; }
	public required IGfxBuffer ClusterLightIndexBuffer { get; init; }

	public required Matrix4x4 ShadowViewProjection0 { get; init; }
	public required Matrix4x4 ShadowViewProjection1 { get; init; }
	public required Matrix4x4 ShadowViewProjection2 { get; init; }
	public required float ShadowSplit0 { get; init; }
	public required float ShadowSplit1 { get; init; }
	public required float ShadowSplit2 { get; init; }
	public required float ShadowCascadeBlendDistance { get; init; }
	public required int ShadowedDirectionalLightIndex { get; init; }
	public required float ShadowDepthBias { get; init; }
	public required float ShadowStrength { get; init; }
	public required bool ShadowsEnabled { get; init; }
	public required float ShadowTexelSizeX { get; init; }
	public required float ShadowTexelSizeY { get; init; }
	public required bool AoEnabled { get; init; }
	public required int ClusterCountX { get; init; }
	public required int ClusterCountY { get; init; }
	public required int ClusterCountZ { get; init; }
	public required float NearPlane { get; init; }
	public required float FarPlane { get; init; }

	public required Int2 DispatchSize { get; init; }
}
