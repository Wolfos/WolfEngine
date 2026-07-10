using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct BloomPassConfig
{
	public required IGfxPipeline Pipeline { get; init; }
	public required DescriptorHandle SourceHandle { get; init; }
	public required DescriptorHandle SecondaryHandle { get; init; }
	public required DescriptorHandle OutputHandle { get; init; }
	public required DescriptorHandle LinearSampler { get; init; }
	public required Int2 SourceSize { get; init; }
	public required Int2 SecondarySize { get; init; }
	public required Int2 OutputSize { get; init; }
	public required BloomConfig Settings { get; init; }
}
