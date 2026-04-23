#nullable enable

using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct GpuDrawBackendFrameSignals
{
	public GpuDrawBackendFrameSignals(bool requiresFullSlotReencode, bool supportsIndirectStructuralUpdates)
	{
		RequiresFullSlotReencode = requiresFullSlotReencode;
		SupportsIndirectStructuralUpdates = supportsIndirectStructuralUpdates;
	}

	public bool RequiresFullSlotReencode { get; }

	public bool SupportsIndirectStructuralUpdates { get; }
}

public interface IGpuDrawBackendBridge
{
	GpuDrawBackendFrameSignals PrepareFrame(
		IGfxDevice device,
		IRenderer renderer,
		GpuDrawResources resources,
		IGfxPipeline? primaryGBufferPipeline);

	void ResetCommand(IGfxIndirectCommandBuffer commandBuffer, uint commandIndex);

	bool TryEncodeIndexedDrawCommand(
		IGfxIndirectCommandBuffer commandBuffer,
		uint commandIndex,
		Mesh mesh,
		in SharedDrawIndirectEncodeResources resources,
		in SharedDrawGraphicsBufferBindings bindings);

	void SampleVisibilityDiagnostics(
		IGfxBuffer? drawCountPerBucketBuffer,
		IGfxBuffer? drawExecutionRangePerBucketBuffer,
		GpuDrawHardeningStats stats);

	void SampleGpuDiagnosticCounters(
		IGfxBuffer? diagnosticsCounterBuffer,
		uint[] lastCounters,
		GpuDrawHardeningStats stats);
}
