#nullable enable

using System;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering.Backend.D3D12;

public sealed class D3D12GpuDrawBackendBridge : IGpuDrawBackendBridge
{
	private ulong _lastIndirectBindingVersion;
	private D3D12Device? _device;
	private int _frameIndex;
	private bool _samplingThisFrame;
	private IGfxBuffer? _counterReadback;
	private IGfxBuffer? _visibleCountReadback;
	private IGfxBuffer? _executionRangeReadback;
	private bool _loggedReadbackFailure;

	public GpuDrawBackendFrameSignals PrepareFrame(
		IGfxDevice device,
		IRenderer renderer,
		GpuDrawResources resources,
		IGfxPipeline? primaryGBufferPipeline)
	{
		if (device is not D3D12Device d3d12Device)
		{
			return new GpuDrawBackendFrameSignals(
				requiresFullSlotReencode: false,
				supportsIndirectStructuralUpdates: false);
		}

		_device = d3d12Device;

		// Reading these buffers means copying out of the default heap and waiting for the copy, so only
		// pay for it on the frames whose numbers actually get reported. Metal reads shared memory and can
		// afford to sample every frame; this cannot.
		_frameIndex++;
		var interval = GraphicsConfig.GpuHardeningLogIntervalFrames;
		_samplingThisFrame = interval > 0 && _frameIndex % interval == 0;

		_ = renderer;
		_ = primaryGBufferPipeline;

		var requiresFullSlotReencode = false;
		if (_lastIndirectBindingVersion != resources.IndirectBindingVersion)
		{
			_lastIndirectBindingVersion = resources.IndirectBindingVersion;
			requiresFullSlotReencode = true;
		}

		return new GpuDrawBackendFrameSignals(
			requiresFullSlotReencode,
			supportsIndirectStructuralUpdates: true);
	}

	public void ResetCommand(IGfxIndirectCommandBuffer commandBuffer, uint commandIndex)
	{
		if (commandBuffer is not D3D12IndirectCommandBuffer d3d12CommandBuffer)
		{
			throw new InvalidOperationException("Indirect command buffer was not created by the Direct3D12 backend.");
		}

		d3d12CommandBuffer.ResetCommand(commandIndex);
	}

	public bool TryEncodeIndexedDrawCommand(
		IGfxIndirectCommandBuffer commandBuffer,
		uint commandIndex,
		uint drawArgsCommandIndex,
		Mesh mesh,
		in SharedDrawIndirectEncodeResources resources,
		GraphicsPassBindingSet passBindings,
		in SharedDrawPerDrawBindings perDrawBindings)
	{
		if (commandBuffer is not D3D12IndirectCommandBuffer d3d12CommandBuffer ||
		    resources.InstanceBuffer is not D3D12Buffer instanceBuffer ||
		    resources.MaterialBuffer is not D3D12Buffer materialBuffer ||
		    resources.DrawArgsBuffer is not D3D12Buffer drawArgsBuffer ||
		    resources.MaterialGenerationBuffer is not D3D12Buffer materialGenerationBuffer)
		{
			return false;
		}
		if (perDrawBindings.InstanceRegisterIndex != 10 || perDrawBindings.MaterialRegisterIndex != 11 ||
			perDrawBindings.DrawArgsRegisterIndex != 12 || perDrawBindings.MaterialGenerationRegisterIndex != 13)
		{
			return false;
		}
		foreach (var binding in passBindings.Bindings)
		{
			if (binding.Resource is not D3D12Buffer ||
				(binding.Kind == GraphicsPassBindingKind.ConstantBuffer && D3D12RootBindings.TryGetGraphicsCbvIndex(binding.RegisterIndex, out _) == false) ||
				(binding.Kind == GraphicsPassBindingKind.StructuredBuffer && D3D12RootBindings.TryGetGraphicsSrvIndex(binding.RegisterIndex, out _) == false))
			{
				return false;
			}
		}

		d3d12CommandBuffer.EncodeIndexedDrawCommand(
			commandIndex,
			mesh,
			instanceBuffer,
			materialBuffer,
			drawArgsBuffer,
			materialGenerationBuffer,
			resources.DrawArgsBaseOffsetBytes,
			drawArgsCommandIndex,
			passBindings,
			perDrawBindings);
		return true;
	}

	public void SampleVisibilityDiagnostics(
		IGfxBuffer? drawCountPerBucketBuffer,
		IGfxBuffer? drawExecutionRangePerBucketBuffer,
		GpuDrawHardeningStats stats)
	{
		if (_samplingThisFrame == false || drawCountPerBucketBuffer is null || drawExecutionRangePerBucketBuffer is null)
		{
			return;
		}

		Span<uint> visibleCounts = stackalloc uint[GpuDrawDiagnosticsSampling.VisibleCountElementCount];
		Span<uint> executionRanges = stackalloc uint[GpuDrawDiagnosticsSampling.ExecutionRangeElementCount];
		if (TryReadBuffer(drawCountPerBucketBuffer, ref _visibleCountReadback, "visible draw counts", visibleCounts) == false ||
		    TryReadBuffer(drawExecutionRangePerBucketBuffer, ref _executionRangeReadback, "execution ranges", executionRanges) == false)
		{
			return;
		}

		GpuDrawDiagnosticsSampling.ApplyVisibilityDiagnostics(visibleCounts, executionRanges, stats);
	}

	public void SampleGpuDiagnosticCounters(
		IGfxBuffer? diagnosticsCounterBuffer,
		uint[] lastCounters,
		GpuDrawHardeningStats stats)
	{
		if (_samplingThisFrame == false || diagnosticsCounterBuffer is null)
		{
			return;
		}

		Span<uint> counters = stackalloc uint[GpuDrawResources.HardeningCounterCount];
		if (TryReadBuffer(diagnosticsCounterBuffer, ref _counterReadback, "hardening counters", counters) == false)
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
	private bool TryReadBuffer(IGfxBuffer source, ref IGfxBuffer? readback, string what, Span<uint> destination)
	{
		var device = _device;
		if (device is null)
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

		var raw = new byte[sizeInBytes];
		readableBuffer.Read(raw);
		for (var i = 0; i < destination.Length; i++)
		{
			destination[i] = BitConverter.ToUInt32(raw, i * sizeof(uint));
		}

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
