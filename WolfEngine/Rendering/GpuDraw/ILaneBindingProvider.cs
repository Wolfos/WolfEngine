using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering;

/// <summary>
/// What a pass supplies, per execution lane, for its draw records: whether the lane is drawn at all, which buffers
/// its records bind, and the pipeline that executes them. A pass gives one to each draw record set it creates.
/// </summary>
public interface ILaneBindingProvider
{
	/// <summary>
	/// Increases each time the pass replaces a pipeline or binding its records capture, such as when its pipelines
	/// are rebuilt after a shader reload. Records encoded against an older generation are stale.
	/// </summary>
	ulong Generation { get; }

	bool IsLaneDrawn(GpuDrawExecutionLaneDefinition lane);

	SharedDrawGraphicsBufferBindings? GetBufferBindings(GpuDrawExecutionLaneDefinition lane);

	GraphicsPassBindingSet? GetPassBindings(GpuDrawExecutionLaneDefinition lane);

	IGfxPipeline? GetPipeline(GpuDrawExecutionLaneDefinition lane);
}
