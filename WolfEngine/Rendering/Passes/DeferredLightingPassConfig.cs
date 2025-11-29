using WolfEngine.Rendering.Abstraction;
using WolfEngine.Mathematics;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// Describes the API-agnostic parameters required to record the deferred lighting compute pass.
/// </summary>
public struct DeferredLightingPassConfig
{
	public required IGfxPipeline Pipeline { get; init; }

	public required IGfxDescriptorSet DescriptorSet { get; init; }

	public required Int2 DispatchSize { get; init; }
}
