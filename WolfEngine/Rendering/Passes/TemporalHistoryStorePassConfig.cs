using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct TemporalHistoryStorePassConfig
{
	public required IGfxPipeline Pipeline { get; init; }
	public required DescriptorHandle SourceColorHandle { get; init; }
	public required DescriptorHandle SourceDepthHandle { get; init; }
	public required DescriptorHandle HistoryColorHandle { get; init; }
	public required DescriptorHandle HistoryDepthHandle { get; init; }
	public required Int2 RenderSize { get; init; }
}
