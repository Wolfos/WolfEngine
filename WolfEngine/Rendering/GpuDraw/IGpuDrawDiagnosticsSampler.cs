using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

/// <summary>
/// Reads the GPU-written hardening diagnostics back to the CPU. Only acquiring the values is backend-specific;
/// <see cref="GpuDrawDiagnosticsSampling"/> interprets them the same way for every backend.
/// </summary>
public interface IGpuDrawDiagnosticsSampler
{
	/// <summary>
	/// Called once per frame while diagnostics are enabled. Reads the visibility diagnostics and the hardening
	/// counters from <paramref name="resources"/> into <paramref name="stats"/>; a backend may skip frames whose
	/// numbers are not reported.
	/// </summary>
	void Sample(
		IGfxDevice device,
		GpuDrawResources resources,
		uint[] lastCounters,
		GpuDrawHardeningStats stats);
}
