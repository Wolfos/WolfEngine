using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

public sealed class SkyboxResources
{
	public required IGfxTexture EnvironmentTexture { get; init; }
	public IGfxTexture IrradianceTexture { get; init; }
	public IGfxTexture PrefilteredEnvironment { get; init; }
	public IGfxTexture BrdfLut { get; init; }
}
