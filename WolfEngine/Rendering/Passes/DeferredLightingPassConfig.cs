using WolfEngine.Rendering.Abstraction;
using WolfEngine.Mathematics;

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
	public required DescriptorHandle SkyboxEnvironment { get; init; }
	public required DescriptorHandle SkyboxIrradiance { get; init; }
	public required DescriptorHandle SkyboxPrefilter { get; init; }
	public required DescriptorHandle SkyboxBrdfLut { get; init; }
	public required DescriptorHandle LightingOutput { get; init; }
	public required DescriptorHandle LinearSampler { get; init; }

	public required Int2 DispatchSize { get; init; }
}
