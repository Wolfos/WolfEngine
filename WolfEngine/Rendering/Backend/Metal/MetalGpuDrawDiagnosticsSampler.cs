using System.Runtime.Versioning;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.Metal;

[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
internal sealed class MetalGpuDrawDiagnosticsSampler : IGpuDrawDiagnosticsSampler
{
	public unsafe void SampleVisibilityDiagnostics(
		IGfxDevice device,
		IGfxBuffer? drawCountPerBucketBuffer,
		IGfxBuffer? drawExecutionRangePerBucketBuffer,
		GpuDrawHardeningStats stats)
	{
		if (drawCountPerBucketBuffer is not MetalBuffer drawCountBuffer ||
		    drawCountBuffer.Buffer.NativePtr == IntPtr.Zero ||
		    drawExecutionRangePerBucketBuffer is not MetalBuffer executionRangeBuffer ||
		    executionRangeBuffer.Buffer.NativePtr == IntPtr.Zero)
		{
			return;
		}

		// Shared memory, so the buffers are read in place; the interpretation is shared with D3D12.
		var visibleCounts = new ReadOnlySpan<uint>(
			(void*)drawCountBuffer.Buffer.Contents.ToPointer(),
			GpuDrawDiagnosticsSampling.VisibleCountElementCount);
		var executionRanges = new ReadOnlySpan<uint>(
			(void*)executionRangeBuffer.Buffer.Contents.ToPointer(),
			GpuDrawDiagnosticsSampling.ExecutionRangeElementCount);
		GpuDrawDiagnosticsSampling.ApplyVisibilityDiagnostics(visibleCounts, executionRanges, stats);
	}

	public unsafe void SampleGpuDiagnosticCounters(
		IGfxDevice device,
		IGfxBuffer? diagnosticsCounterBuffer,
		uint[] lastCounters,
		GpuDrawHardeningStats stats)
	{
		if (diagnosticsCounterBuffer is not MetalBuffer diagnosticsBuffer ||
		    diagnosticsBuffer.Buffer.NativePtr == IntPtr.Zero ||
		    lastCounters is null ||
		    lastCounters.Length < GpuDrawResources.HardeningCounterCount)
		{
			return;
		}

		var counters = new ReadOnlySpan<uint>(
			(void*)diagnosticsBuffer.Buffer.Contents.ToPointer(),
			GpuDrawResources.HardeningCounterCount);
		GpuDrawDiagnosticsSampling.ApplyDiagnosticCounters(counters, lastCounters, stats);
	}
}
