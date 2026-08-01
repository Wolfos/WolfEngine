using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct Fsr3AccumulatePassConfig
{
	public required IGfxPipeline Pipeline { get; init; }
	public required DescriptorHandle ExposureHandle { get; init; }
	public required DescriptorHandle InputColorHandle { get; init; }
	public required DescriptorHandle DilatedMotionVectorsHandle { get; init; }
	public required DescriptorHandle DilatedReactiveMasksHandle { get; init; }
	public required DescriptorHandle FarthestDepthMip1Handle { get; init; }
	public required DescriptorHandle LumaInstabilityHandle { get; init; }
	public required DescriptorHandle NewLocksHandle { get; init; }
	public required DescriptorHandle HistoryReadHandle { get; init; }
	public required DescriptorHandle HistoryWriteHandle { get; init; }
	public required DescriptorHandle PointSampler { get; init; }
	public required DescriptorHandle LinearSampler { get; init; }
	public required Fsr3ConstantValues Constants { get; init; }
}
