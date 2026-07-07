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
	public required DescriptorHandle DdgiIrradianceL0 { get; init; }
	public required DescriptorHandle DdgiIrradianceLy { get; init; }
	public required DescriptorHandle DdgiIrradianceLz { get; init; }
	public required DescriptorHandle DdgiIrradianceLx { get; init; }
	public required DescriptorHandle DdgiVisibility { get; init; }
	public required DescriptorHandle DdgiProbeState { get; init; }
	public required DescriptorHandle DdgiProbeActivity { get; init; }
	public required DescriptorHandle DdgiProbeRelocationDecision { get; init; }
	public required DescriptorHandle DdgiFinalContribution { get; init; }
	public required DescriptorHandle DdgiProbeBaseWeightDebug { get; init; }
	public required DescriptorHandle DdgiWeightedVisibilityDebug { get; init; }
	public required DescriptorHandle DdgiDominantProbeDebug { get; init; }
	public required DescriptorHandle DdgiDominantProbeCoordDebug { get; init; }
	public required DescriptorHandle DdgiProbeRelocationDebug { get; init; }
	public required DescriptorHandle DdgiProbeRelocationDecisionDebug { get; init; }
	public required bool DdgiFinalContributionDebugEnabled { get; init; }
	public required bool DdgiProbeDebugEnabled { get; init; }
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
	public required Vector3 ShadowDepthBiases { get; init; }
	public required float ShadowStrength { get; init; }
	public required bool ShadowsEnabled { get; init; }
	public required float ShadowMaxDistance { get; init; }
	public required float ShadowTexelSizeX { get; init; }
	public required float ShadowTexelSizeY { get; init; }
	public required bool AoEnabled { get; init; }
	public required bool DdgiEnabled { get; init; }
	public required Vector3 DdgiOrigin { get; init; }
	public required Int3 DdgiStorageOffset { get; init; }
	public required Int3 DdgiScrollDelta { get; init; }
	public required float DdgiProbeSpacing { get; init; }
	public required int DdgiProbeCountX { get; init; }
	public required int DdgiProbeCountY { get; init; }
	public required int DdgiProbeCountZ { get; init; }
	public required int DdgiProbeCount { get; init; }
	public required int DdgiAtlasColumns { get; init; }
	public required int DdgiAtlasRows { get; init; }
	public required float DdgiMaxRayDistance { get; init; }
	public required float DdgiViewBias { get; init; }
	public required float DdgiHorizontalBlendDistance { get; init; }
	public required float DdgiVerticalBlendDistance { get; init; }
	public required bool DdgiProbeRelocationEnabled { get; init; }
	public required int ClusterCountX { get; init; }
	public required int ClusterCountY { get; init; }
	public required int ClusterCountZ { get; init; }
	public required float NearPlane { get; init; }
	public required float FarPlane { get; init; }

	public required Int2 DispatchSize { get; init; }
}
