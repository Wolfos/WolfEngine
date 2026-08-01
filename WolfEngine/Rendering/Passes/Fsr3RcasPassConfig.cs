using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct Fsr3RcasPassConfig
{
	public required IGfxPipeline Pipeline { get; init; }

	/// <summary>SRV bound to <c>rcasInputHandle</c>: the accumulate pass's output at display resolution.</summary>
	public required DescriptorHandle InputHandle { get; init; }

	/// <summary>UAV bound to <c>upscaledOutputHandle</c>: final upscaled colour at display resolution.</summary>
	public required DescriptorHandle OutputHandle { get; init; }

	/// <summary>SRV bound to <c>exposureHandle</c>: a 1x1 texture whose red channel holds the exposure scale.</summary>
	public required DescriptorHandle ExposureHandle { get; init; }

	public required DescriptorHandle PointSampler { get; init; }
	public required DescriptorHandle LinearSampler { get; init; }

	/// <summary>Shared FSR3 constants. RCAS reads only <c>UpscaleSize()</c>, but the block is common to every pass.</summary>
	public required Fsr3ConstantValues Constants { get; init; }

	/// <summary>Display resolution. RCAS reads and writes at this size.</summary>
	public Int2 UpscaleSize => Constants.UpscaleSize;

	/// <summary>
	/// Sharpness in 0..1, matching <c>FfxFsr3UpscalerDispatchDescription.sharpness</c>, where 0 is
	/// no additional sharpening and 1 is maximum. The pass remaps this to the stops-based scale
	/// RCAS actually consumes.
	/// </summary>
	public required float Sharpness { get; init; }

	/// <summary>
	/// False keeps this dispatch as an exact copy. FSR3 normally implements this as an
	/// accumulate-pass permutation; the copy path avoids an extra shader variant here.
	/// </summary>
	public required bool Enabled { get; init; }
}
