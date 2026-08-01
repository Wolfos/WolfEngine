using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct Fsr3ShadingChangePyramidPassConfig
{
	public required IGfxPipeline Pipeline { get; init; }

	// Inputs at render resolution.
	public required DescriptorHandle CurrentLumaHandle { get; init; }
	public required DescriptorHandle PreviousLumaHandle { get; init; }
	public required DescriptorHandle DilatedMotionVectorsHandle { get; init; }

	/// <summary>1x1 exposure SRV. In practice the frame-info target from the luma pyramid.</summary>
	public required DescriptorHandle ExposureHandle { get; init; }

	/// <summary>1x1 R32Uint SPD counter. Must be zero before the dispatch.</summary>
	public required DescriptorHandle SpdGlobalAtomicHandle { get; init; }

	/// <summary>UAVs for the six pyramid levels, level 0 at half render resolution.</summary>
	public required DescriptorHandle[] SpdMipHandles { get; init; }

	public required DescriptorHandle PointSampler { get; init; }
	public required DescriptorHandle LinearSampler { get; init; }

	public required Fsr3ConstantValues Constants { get; init; }
	public required Fsr3SpdSetup SpdSetup { get; init; }
}

public readonly struct Fsr3ShadingChangePassConfig
{
	public required IGfxPipeline Pipeline { get; init; }

	/// <summary>SRVs for the pyramid levels the pass samples.</summary>
	public required DescriptorHandle[] SpdMipReadHandles { get; init; }

	/// <summary>UAV at half render resolution.</summary>
	public required DescriptorHandle ShadingChangeHandle { get; init; }

	public required DescriptorHandle PointSampler { get; init; }
	public required DescriptorHandle LinearSampler { get; init; }

	public required Fsr3ConstantValues Constants { get; init; }
}
