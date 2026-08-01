using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct Fsr3LumaPyramidPassConfig
{
	public required IGfxPipeline Pipeline { get; init; }

	// Inputs, produced by prepare_inputs at render resolution.
	public required DescriptorHandle CurrentLumaHandle { get; init; }
	public required DescriptorHandle FarthestDepthHandle { get; init; }

	// Outputs.
	public required DescriptorHandle FarthestDepthMip1Handle { get; init; }

	/// <summary>1x1 target holding exposure, log luma, and scene average luma.</summary>
	public required DescriptorHandle FrameInfoHandle { get; init; }

	/// <summary>1x1 R32Uint counter used to elect the last thread group. Must start at zero.</summary>
	public required DescriptorHandle SpdGlobalAtomicHandle { get; init; }

	/// <summary>The six pyramid mips. Mip 5 is read back across thread groups.</summary>
	public required DescriptorHandle[] SpdMipHandles { get; init; }

	public required DescriptorHandle PointSampler { get; init; }
	public required DescriptorHandle LinearSampler { get; init; }

	public required Fsr3ConstantValues Constants { get; init; }
	public required Fsr3SpdSetup SpdSetup { get; init; }
}
