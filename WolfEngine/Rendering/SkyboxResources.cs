using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

public sealed class SkyboxResources
{
	public required IGfxPipeline Pipeline { get; init; }
	public required DescriptorHandle EnvironmentHandle { get; init; }
	public required DescriptorHandle Sampler { get; init; }
	public required Mesh Mesh { get; init; }
	public required IGfxTexture EnvironmentTexture { get; init; }
	public IGfxTexture IrradianceTexture { get; init; }
	public IGfxTexture PrefilteredEnvironment { get; init; }
	public IGfxTexture BrdfLut { get; init; }
}
