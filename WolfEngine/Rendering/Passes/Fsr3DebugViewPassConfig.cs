using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct Fsr3DebugViewPassConfig
{
	public required IGfxPipeline Pipeline { get; init; }
	public required DescriptorHandle DilatedReactiveMasksHandle { get; init; }
	public required DescriptorHandle DilatedMotionVectorsHandle { get; init; }
	public required DescriptorHandle DilatedDepthHandle { get; init; }
	public required DescriptorHandle InternalUpscaledColorHandle { get; init; }
	public required DescriptorHandle CurrentLumaHandle { get; init; }
	public required DescriptorHandle PreviousLumaHandle { get; init; }
	public required DescriptorHandle OutputHandle { get; init; }
	public required DescriptorHandle ExposureHandle { get; init; }
	public required DescriptorHandle PointSampler { get; init; }
	public required DescriptorHandle LinearSampler { get; init; }
	public required Fsr3ConstantValues Constants { get; init; }
}
