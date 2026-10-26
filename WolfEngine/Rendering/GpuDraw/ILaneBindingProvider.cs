using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering;

/// <summary>
/// What a pass supplies, per execution lane, for its draw records: whether the lane is drawn at all, which buffers
/// its records bind, and the pipeline that executes them.
/// </summary>
public interface ILaneBindingProvider
{
	bool IsLaneDrawn(GpuDrawExecutionLaneDefinition lane);

	SharedDrawGraphicsBufferBindings? GetBufferBindings(GpuDrawExecutionLaneDefinition lane);

	GraphicsPassBindingSet? GetPassBindings(GpuDrawExecutionLaneDefinition lane);

	IGfxPipeline? GetPipeline(GpuDrawExecutionLaneDefinition lane);
}
