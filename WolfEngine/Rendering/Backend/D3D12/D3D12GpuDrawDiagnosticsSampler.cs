using System.Runtime.InteropServices;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.D3D12;

public sealed class D3D12GpuDrawDiagnosticsSampler : IGpuDrawDiagnosticsSampler
{
	private int _frameIndex;
	private IGfxBuffer? _counterReadback;
	private IGfxBuffer? _visibleCountReadback;
	private IGfxBuffer? _executionRangeReadback;
	private bool _loggedReadbackFailure;

	public void Sample(
		IGfxDevice device,
		GpuDrawResources resources,
		uint[] lastCounters,
		GpuDrawHardeningStats stats)
	{
		ArgumentNullException.ThrowIfNull(resources);

		// Reading these buffers means copying out of the default heap and waiting for the copy, so only
		// pay for it on the frames whose numbers actually get reported. Metal reads shared memory and can
		// afford to sample every frame; this cannot.
		_frameIndex++;
		var interval = GraphicsConfig.GpuHardeningLogIntervalFrames;
		if (interval <= 0 || _frameIndex % interval != 0)
		{
			return;
		}

		SampleVisibilityDiagnostics(device, resources.DrawCountPerBucketBuffer, resources.DrawExecutionRangePerBucketBuffer, stats);
		SampleGpuDiagnosticCounters(device, resources.DiagnosticsCounterBuffer, lastCounters, stats);
	}

	private void SampleVisibilityDiagnostics(
		IGfxDevice device,
		IGfxBuffer? drawCountPerBucketBuffer,
		IGfxBuffer? drawExecutionRangePerBucketBuffer,
		GpuDrawHardeningStats stats)
	{
		if (drawCountPerBucketBuffer is null || drawExecutionRangePerBucketBuffer is null)
		{
			return;
		}

		Span<uint> visibleCounts = stackalloc uint[GpuDrawDiagnosticsSampling.VisibleCountElementCount];
		Span<uint> executionRanges = stackalloc uint[GpuDrawDiagnosticsSampling.ExecutionRangeElementCount];
		if (TryReadBuffer(device, drawCountPerBucketBuffer, ref _visibleCountReadback, "visible draw counts", visibleCounts) == false ||
		    TryReadBuffer(device, drawExecutionRangePerBucketBuffer, ref _executionRangeReadback, "execution ranges", executionRanges) == false)
		{
			return;
		}

		GpuDrawDiagnosticsSampling.ApplyVisibilityDiagnostics(visibleCounts, executionRanges, stats);
	}

	private void SampleGpuDiagnosticCounters(
		IGfxDevice device,
		IGfxBuffer? diagnosticsCounterBuffer,
		uint[] lastCounters,
		GpuDrawHardeningStats stats)
	{
		if (diagnosticsCounterBuffer is null)
		{
			return;
		}

		Span<uint> counters = stackalloc uint[GpuDrawResources.HardeningCounterCount];
		if (TryReadBuffer(device, diagnosticsCounterBuffer, ref _counterReadback, "hardening counters", counters) == false)
		{
			return;
		}

		GpuDrawDiagnosticsSampling.ApplyDiagnosticCounters(counters, lastCounters, stats);
	}

	/// <summary>
	/// Copies a default-heap buffer into a readback buffer and reads it. This waits for the copy, which is
	/// why it is confined to reporting frames: the alternative is a multi-frame ring, and stale hardening
	/// numbers are worse than an occasional stall on a diagnostic that is off by default.
	/// </summary>
	private bool TryReadBuffer(IGfxDevice device, IGfxBuffer source, ref IGfxBuffer? readback, string what, Span<uint> destination)
	{
		if (device is not D3D12Device)
		{
			return false;
		}

		var sizeInBytes = (ulong)destination.Length * sizeof(uint);
		if (source.Descriptor.SizeInBytes < sizeInBytes)
		{
			LogReadbackFailureOnce($"'{what}' buffer is {source.Descriptor.SizeInBytes} bytes, expected at least {sizeInBytes}.");
			return false;
		}

		readback ??= device.CreateBuffer(new BufferDescriptor(
			sizeInBytes,
			BufferUsage.Staging,
			BufferFlags.None,
			$"HardeningReadback_{what.Replace(" ", string.Empty)}"));
		if (readback is not IReadableGpuBuffer readableBuffer)
		{
			LogReadbackFailureOnce($"readback buffer for '{what}' does not support CPU reads.");
			return false;
		}

		var commandList = device.BeginCompute();
		try
		{
			commandList.CopyBuffer(source, 0, readback, 0, sizeInBytes);
		}
		finally
		{
			device.Submit(commandList);
			device.WaitForIdle();
		}

		readableBuffer.Read(MemoryMarshal.AsBytes(destination));
		return true;
	}

	private void LogReadbackFailureOnce(string reason)
	{
		if (_loggedReadbackFailure)
		{
			return;
		}

		_loggedReadbackFailure = true;
		Console.WriteLine($"[GpuHardening] diagnostics readback unavailable: {reason}");
	}
}
