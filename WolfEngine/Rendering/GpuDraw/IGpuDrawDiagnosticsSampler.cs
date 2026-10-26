using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

/// <summary>
/// Reads the GPU-written hardening diagnostics back to the CPU. Only acquiring the values is backend-specific;
/// <see cref="GpuDrawDiagnosticsSampling"/> interprets them the same way for every backend.
/// </summary>
public interface IGpuDrawDiagnosticsSampler
{
	/// <summary>Called once per frame while diagnostics are enabled, before <see cref="SampleGpuDiagnosticCounters"/>.</summary>
	void SampleVisibilityDiagnostics(
		IGfxDevice device,
		IGfxBuffer? drawCountPerBucketBuffer,
		IGfxBuffer? drawExecutionRangePerBucketBuffer,
		GpuDrawHardeningStats stats);

	void SampleGpuDiagnosticCounters(
		IGfxDevice device,
		IGfxBuffer? diagnosticsCounterBuffer,
		uint[] lastCounters,
		GpuDrawHardeningStats stats);
}
