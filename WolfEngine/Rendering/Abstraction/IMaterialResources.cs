namespace WolfEngine.Rendering.Abstraction;

public interface IMaterialResources
{
	IGfxPipeline Pipeline { get; }
	DescriptorHandle AlbedoTexture { get; }
	DescriptorHandle OrmTexture { get; }
	DescriptorHandle NormalTexture { get; }
	DescriptorHandle EmissiveTexture { get; }
	DescriptorHandle Sampler { get; }
}
