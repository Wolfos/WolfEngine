using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// API-agnostic parameters for one stage of the DDGI final gather's SVGF denoiser. Each stage reads and writes a
/// different subset of the handles; the rest stay invalid.
/// </summary>
public readonly struct DdgiFinalGatherSvgfPassConfig
{
	public required IGfxPipeline Pipeline { get; init; }
	public required DdgiFinalGatherSvgfPass.Stage Stage { get; init; }
	public required DescriptorHandle Depth { get; init; }
	public required DescriptorHandle Normal { get; init; }
	public required DescriptorHandle Velocity { get; init; }
	public required DescriptorHandle Trace { get; init; }
	public required DescriptorHandle ColorHistory { get; init; }
	public required DescriptorHandle MomentsHistory { get; init; }
	public required DescriptorHandle DepthHistory { get; init; }
	public required DescriptorHandle NormalHistory { get; init; }
	public required DescriptorHandle Integrated { get; init; }
	public required DescriptorHandle Moments { get; init; }
	public required DescriptorHandle GuideDepth { get; init; }
	public required DescriptorHandle GuideNormal { get; init; }
	public required DescriptorHandle Source { get; init; }
	public required DescriptorHandle Output { get; init; }
	public required Int2 FramebufferSize { get; init; }
	public required Int2 TraceSize { get; init; }
	public required bool HistoryValid { get; init; }
	public required float TemporalAlpha { get; init; }
	public required int StepSize { get; init; }
}
