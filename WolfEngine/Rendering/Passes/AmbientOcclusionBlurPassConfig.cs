using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct AmbientOcclusionBlurPassConfig
{
	public required IGfxPipeline Pipeline { get; init; }
	public required DescriptorHandle DepthHandle { get; init; }
	public required DescriptorHandle NormalHandle { get; init; }
	public required DescriptorHandle SourceHandle { get; init; }
	public required DescriptorHandle OutputHandle { get; init; }
	public required Int2 FullResolution { get; init; }
	public required Int2 AoResolution { get; init; }
	public required float BlurSharpness { get; init; }
	public required bool BlurHorizontally { get; init; }
}
