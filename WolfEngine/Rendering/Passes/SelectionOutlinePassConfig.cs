using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct SelectionOutlinePassConfig
{
	public int TargetWidth { get; init; }
	public int TargetHeight { get; init; }
	public int DepthWidth { get; init; }
	public int DepthHeight { get; init; }
	public IGfxTexture TargetColor { get; init; }
	public IGfxTexture DepthTexture { get; init; }
	public IGfxPipeline Pipeline { get; init; }
}
