using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct MotionVectorDebugPassConfig
{
	public required IGfxPipeline Pipeline { get; init; }
	public required DescriptorHandle VelocityHandle { get; init; }
	public required DescriptorHandle OutputHandle { get; init; }
	public required Int2 RenderSize { get; init; }
	public required float MaxPixelsPerFrame { get; init; }
	public required int LegendRadiusPixels { get; init; }
}
