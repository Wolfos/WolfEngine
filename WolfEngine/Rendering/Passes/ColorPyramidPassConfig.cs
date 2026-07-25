using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// API-agnostic parameters for a single color pyramid level build.
/// </summary>
public readonly struct ColorPyramidPassConfig
{
	public required IGfxPipeline Pipeline { get; init; }
	public required DescriptorHandle SourceHandle { get; init; }
	public required DescriptorHandle OutputHandle { get; init; }
	public required DescriptorHandle LinearSampler { get; init; }
	public required Int2 SourceSize { get; init; }
	public required Int2 OutputSize { get; init; }
}
