using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct CasSharpenPassConfig
{
	public required IGfxPipeline Pipeline { get; init; }
	public required DescriptorHandle InputHandle { get; init; }
	public required DescriptorHandle OutputHandle { get; init; }
	public required Int2 RenderSize { get; init; }
	public required bool SharpenEnabled { get; init; }
	public required float Sharpness { get; init; }
}
