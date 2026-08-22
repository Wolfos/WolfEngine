using SharpMetal.Metal;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Backend.Metal;

internal class MtlMaterialResources: IMaterialResources
{
	public required IGfxPipeline Pipeline { get; init; }

	public DescriptorHandle AlbedoTexture { get; init; }

	public DescriptorHandle OrmTexture { get; init; }

	public DescriptorHandle NormalTexture { get; init; }

	public DescriptorHandle EmissiveTexture { get; init; }

	public DescriptorHandle Sampler { get; init; }

	// Internal Metal-specific properties
	internal MTLRenderPipelineState PipelineState { get; init; }
}
