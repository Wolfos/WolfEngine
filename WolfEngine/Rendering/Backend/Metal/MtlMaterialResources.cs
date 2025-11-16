#nullable enable

using SharpMetal.Metal;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Backend.Metal;

internal class MtlMaterialResources: IMaterialResources
{
	public required IGfxPipeline Pipeline { get; init; }
	
	public required IGfxBuffer? ConstantBuffer { get; init; }

	// Internal Metal-specific properties
	internal MTLRenderPipelineState PipelineState { get; init; }

	internal MTLBuffer ColorBuffer { get; init; }
}