using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

public sealed class SkyboxResources
{
	public required IGfxTexture EnvironmentTexture { get; init; }
	public required IGfxTexture IrradianceTexture { get; init; }
	public required IGfxTexture PrefilteredEnvironment { get; init; }
	public required IGfxTexture BrdfLut { get; init; }
}
