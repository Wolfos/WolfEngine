namespace WolfEngine.Rendering.Abstraction;

public interface IMaterialResources
{
	IGfxPipeline Pipeline { get; }
	IGfxBuffer ConstantBuffer { get; }
	DescriptorHandle AlbedoTexture { get; }
	DescriptorHandle MetallicRoughnessTexture { get; }
	DescriptorHandle NormalTexture { get; }
	DescriptorHandle OcclusionTexture { get; }
	DescriptorHandle EmissiveTexture { get; }
	DescriptorHandle Sampler { get; }
}
