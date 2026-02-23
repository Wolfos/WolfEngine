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
	public required DescriptorHandle ShadowMapDepth { get; init; }
	public required DescriptorHandle SkyboxEnvironment { get; init; }
	public required DescriptorHandle SkyboxIrradiance { get; init; }
	public required DescriptorHandle SkyboxPrefilter { get; init; }
	public required DescriptorHandle SkyboxBrdfLut { get; init; }
	public required DescriptorHandle LightingOutput { get; init; }
	public required DescriptorHandle LinearSampler { get; init; }
	public required DescriptorHandle ShadowSampler { get; init; }

	public required Matrix4x4 ShadowViewProjection { get; init; }
	public required int ShadowedDirectionalLightIndex { get; init; }
	public required float ShadowDepthBias { get; init; }
	public required float ShadowStrength { get; init; }
	public required bool ShadowsEnabled { get; init; }
	public required float ShadowTexelSizeX { get; init; }
	public required float ShadowTexelSizeY { get; init; }

	public required Int2 DispatchSize { get; init; }
}
