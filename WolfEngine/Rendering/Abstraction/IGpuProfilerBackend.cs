using WolfEngine.Profiling;

namespace WolfEngine.Rendering.Abstraction;

public interface IGpuProfilerDevice
{
	IGpuProfilerBackend GpuProfilerBackend { get; }
}

public interface IGpuProfilerBackend
{
	bool IsSupported { get; }
	string? UnsupportedReason { get; }
}

internal interface IGpuProfilerCaptureBackend : IGpuProfilerBackend
{
	void Attach(IGfxCommandList commandList, GpuProfilePassCapture passCapture);
}
