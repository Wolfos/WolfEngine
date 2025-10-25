using SharpMetal.Metal;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Backend.Metal;

internal class MtlMaterialResources: IMaterialResources
{
	public MTLRenderPipelineState PipelineState { get; set; }

	public MTLBuffer ColorBuffer { get; set; }
}