using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct TemporalAntiAliasingPassConfig
{
	public required IGfxPipeline Pipeline { get; init; }
	public required DescriptorHandle CurrentColorHandle { get; init; }
	public required DescriptorHandle VelocityHandle { get; init; }
	public required DescriptorHandle NormalHandle { get; init; }
	public required DescriptorHandle MaterialHandle { get; init; }
	public required DescriptorHandle CurrentDepthHandle { get; init; }
	public required DescriptorHandle HistoryColorHandle { get; init; }
	public required DescriptorHandle HistoryDepthHandle { get; init; }
	public required DescriptorHandle OutputHandle { get; init; }
	public required DescriptorHandle LinearSampler { get; init; }
	public required Int2 RenderSize { get; init; }
	public required Vector2 CurrentJitterPixels { get; init; }
	public required Vector2 PreviousJitterPixels { get; init; }
	public required Matrix4x4 InverseUnjitteredViewProjection { get; init; }
	public required Matrix4x4 PreviousViewProjection { get; init; }
	public required float CurrentProjectionZBias { get; init; }
	public required float CurrentProjectionZScale { get; init; }
	public required float PreviousProjectionZBias { get; init; }
	public required float PreviousProjectionZScale { get; init; }
	public required TemporalAntiAliasingConfig Settings { get; init; }
	public required bool HistoryValid { get; init; }
	public required bool ResetHistory { get; init; }
}
