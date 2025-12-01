using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

public sealed class SkyboxResources
{
	public required IGfxPipeline Pipeline { get; init; }
	public required IGfxDescriptorSet DescriptorSet { get; init; }
	public required Mesh Mesh { get; init; }
	public required IGfxTexture EnvironmentTexture { get; init; }
}
