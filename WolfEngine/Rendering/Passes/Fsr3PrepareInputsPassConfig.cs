using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct Fsr3PrepareInputsPassConfig
{
	public required IGfxPipeline Pipeline { get; init; }

	// Inputs, all at render resolution.
	public required DescriptorHandle InputColorHandle { get; init; }
	public required DescriptorHandle InputDepthHandle { get; init; }
	public required DescriptorHandle InputMotionVectorsHandle { get; init; }

	// Outputs, all at render resolution.
	public required DescriptorHandle DilatedMotionVectorsHandle { get; init; }
	public required DescriptorHandle DilatedDepthHandle { get; init; }
	public required DescriptorHandle FarthestDepthHandle { get; init; }
	public required DescriptorHandle CurrentLumaHandle { get; init; }

	/// <summary>
	/// UAV over the R32Uint scatter target for the previous-frame nearest depth. Reached in the
	/// shader through <c>GetRWTextureUint</c>, because the write is an integer atomic.
	/// </summary>
	public required DescriptorHandle ReconstructedPrevNearestDepthHandle { get; init; }

	public required DescriptorHandle PointSampler { get; init; }
	public required DescriptorHandle LinearSampler { get; init; }

	public required Fsr3ConstantValues Constants { get; init; }
}
