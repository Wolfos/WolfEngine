using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct AmbientOcclusionPassConfig
{
	public required IGfxPipeline Pipeline { get; init; }
	public required AmbientOcclusionMode Mode { get; init; }
	public required DescriptorHandle DepthHandle { get; init; }
	public required DescriptorHandle NormalHandle { get; init; }
	public required DescriptorHandle OutputHandle { get; init; }
	public DescriptorHandle RayTracingHitMaskHandle { get; init; }
	public DescriptorHandle RayTracingHitDistanceHandle { get; init; }
	public IGfxTopLevelAccelerationStructure? TopLevelAccelerationStructure { get; init; }
	public required Int2 FullResolution { get; init; }
	public required Int2 OutputResolution { get; init; }
	public required int SliceCount { get; init; }
	public required int StepCount { get; init; }
	public required float Radius { get; init; }
	public required float Thickness { get; init; }
	public required float Bias { get; init; }
	public required float Strength { get; init; }
	public required float Power { get; init; }
	public required uint FrameIndex { get; init; }
}
