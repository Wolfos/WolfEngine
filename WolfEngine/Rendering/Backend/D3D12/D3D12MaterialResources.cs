using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.D3D12;

internal class D3D12MaterialResources: IMaterialResources
{
	public required IGfxPipeline Pipeline { get; init; }
	public DescriptorHandle AlbedoTexture { get; init; }
	public DescriptorHandle OrmTexture { get; init; }
	public DescriptorHandle NormalTexture { get; init; }
	public DescriptorHandle EmissiveTexture { get; init; }
	public DescriptorHandle Sampler { get; init; }
}
