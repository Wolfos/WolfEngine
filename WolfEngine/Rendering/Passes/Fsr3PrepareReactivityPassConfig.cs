using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct Fsr3PrepareReactivityPassConfig
{
	public required IGfxPipeline Pipeline { get; init; }

	public required DescriptorHandle ReconstructedPrevNearestDepthHandle { get; init; }
	public required DescriptorHandle DilatedMotionVectorsHandle { get; init; }
	public required DescriptorHandle DilatedDepthHandle { get; init; }
	public required DescriptorHandle ReactiveMaskHandle { get; init; }
	public required DescriptorHandle TransparencyAndCompositionMaskHandle { get; init; }
	public required DescriptorHandle AccumulationReadHandle { get; init; }
	public required DescriptorHandle ShadingChangeHandle { get; init; }
	public required DescriptorHandle CurrentLumaHandle { get; init; }
	public required DescriptorHandle ExposureHandle { get; init; }

	public required DescriptorHandle DilatedReactiveMasksHandle { get; init; }
	public required DescriptorHandle NewLocksHandle { get; init; }
	public required DescriptorHandle AccumulationWriteHandle { get; init; }

	public required DescriptorHandle PointSampler { get; init; }
	public required DescriptorHandle LinearSampler { get; init; }

	public required Fsr3ConstantValues Constants { get; init; }
	public required float AlphaTestReactiveScale { get; init; }
	public required float TransparencyAndCompositionMaskScale { get; init; }
}
