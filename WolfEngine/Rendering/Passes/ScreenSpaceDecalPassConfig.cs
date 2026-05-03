#nullable enable

using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public struct ScreenSpaceDecalPassConfig
{
	public required int FramebufferWidth { get; init; }
	public required int FramebufferHeight { get; init; }
	public required IGfxTexture SourceAlbedo { get; init; }
	public required IGfxTexture SourceNormal { get; init; }
	public required IGfxTexture SourceMaterial { get; init; }
	public required IGfxTexture SourceEmissive { get; init; }
	public required IGfxTexture DepthTexture { get; init; }
	public required IGfxTexture TargetAlbedo { get; init; }
	public required IGfxTexture TargetNormal { get; init; }
	public required IGfxTexture TargetMaterial { get; init; }
	public required IGfxTexture TargetEmissive { get; init; }
	public required IGfxPipeline Pipeline { get; init; }
	public required IGfxBuffer? DecalProjectorBuffer { get; init; }
	public required uint DecalProjectorCount { get; init; }
}
