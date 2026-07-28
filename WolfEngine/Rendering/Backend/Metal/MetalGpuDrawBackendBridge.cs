#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering.Backend.Metal;

[SupportedOSPlatform("macos")]
internal sealed class MetalGpuDrawBackendBridge : IGpuDrawBackendBridge
{
	private nint _lastBindlessCountBufferPtr;
	private nint _lastBindlessTextureBufferPtr;
	private nint _lastBindlessRwTextureBufferPtr;
	private nint _lastBindlessSamplerBufferPtr;
	private ulong _lastIndirectBindingVersion;
	private MetalDescriptorTable? _descriptorTable;

	public GpuDrawBackendFrameSignals PrepareFrame(
		IGfxDevice device,
		IRenderer renderer,
		GpuDrawResources resources,
		IGfxPipeline? primaryGBufferPipeline)
	{
		if (device is not MetalDevice || device.GlobalTable is not MetalDescriptorTable metalTable)
		{
			_descriptorTable = null;
			return new GpuDrawBackendFrameSignals(requiresFullSlotReencode: false, supportsIndirectStructuralUpdates: false);
		}

		_descriptorTable = metalTable;

		if (primaryGBufferPipeline is MetalPipeline metalPipeline)
		{
			metalTable.SetArgumentEncoders(
				metalPipeline.TextureEncoder,
				metalPipeline.RWTextureEncoder,
				metalPipeline.SamplerEncoder);
		}

		var requiresFullSlotReencode = false;
		if (renderer is WolfRendererMetal metalRenderer &&
		    metalRenderer.ConsumePackedGeometryRefresh())
		{
			requiresFullSlotReencode = true;
		}

		if (BindlessPointersChanged(metalTable))
		{
			CacheBindlessPointers(metalTable);
			requiresFullSlotReencode = true;
		}
		if (_lastIndirectBindingVersion != resources.IndirectBindingVersion)
		{
			_lastIndirectBindingVersion = resources.IndirectBindingVersion;
			requiresFullSlotReencode = true;
		}

		return new GpuDrawBackendFrameSignals(requiresFullSlotReencode, supportsIndirectStructuralUpdates: true);
	}

	public void ResetCommand(IGfxIndirectCommandBuffer commandBuffer, uint commandIndex)
	{
		if (commandBuffer is not MetalIndirectCommandBuffer metalCommandBuffer)
		{
			throw new InvalidOperationException("Indirect command buffer was not created by the Metal backend.");
		}

		metalCommandBuffer.ResetCommand(commandIndex);
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
		if (_descriptorTable is null ||
		    commandBuffer is not MetalIndirectCommandBuffer indirectCommands ||
		    mesh.VertexBuffer is not MetalBuffer metalVertexBuffer ||
		    mesh.IndexBuffer is not MetalBuffer metalIndexBuffer ||
		    resources.InstanceBuffer is not MetalBuffer instanceBuffer ||
		    resources.MaterialBuffer is not MetalBuffer materialBuffer ||
		    resources.DrawArgsBuffer is not MetalBuffer drawArgsBuffer ||
		    resources.MaterialGenerationBuffer is not MetalBuffer materialGenerationBuffer)
		{
			return false;
		}
		foreach (var binding in passBindings.Bindings)
		{
			if (binding.Resource is not MetalBuffer)
				return false;
		}

		if (_descriptorTable.CountBuffer.NativePtr == IntPtr.Zero ||
		    _descriptorTable.TextureArgumentBuffer.NativePtr == IntPtr.Zero ||
		    _descriptorTable.SamplerArgumentBuffer.NativePtr == IntPtr.Zero)
		{
			return false;
		}

		indirectCommands.EncodeIndexedDrawCommand(
			commandIndex,
			metalVertexBuffer,
			mesh.PackedVertexOffsetBytes,
			metalIndexBuffer,
			IndexFormat.UInt32,
			mesh.IndexCount,
			mesh.PackedIndexOffsetBytes,
			0,
			resources.DrawArgsBaseOffsetBytes + (drawArgsCommandIndex * (ulong)Marshal.SizeOf<GpuDrawArgs>()),
			instanceBuffer,
			materialBuffer,
			materialGenerationBuffer,
			drawArgsBuffer,
			passBindings,
			perDrawBindings,
			_descriptorTable.CountBuffer,
			_descriptorTable.TextureArgumentBuffer,
			_descriptorTable.RWTextureArgumentBuffer,
			_descriptorTable.SamplerArgumentBuffer);
		return true;
	}

	public unsafe void SampleVisibilityDiagnostics(
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

	private bool BindlessPointersChanged(MetalDescriptorTable table)
	{
		return _lastBindlessCountBufferPtr != table.CountBuffer.NativePtr ||
		       _lastBindlessTextureBufferPtr != table.TextureArgumentBuffer.NativePtr ||
		       _lastBindlessRwTextureBufferPtr != table.RWTextureArgumentBuffer.NativePtr ||
		       _lastBindlessSamplerBufferPtr != table.SamplerArgumentBuffer.NativePtr;
	}

	private void CacheBindlessPointers(MetalDescriptorTable table)
	{
		_lastBindlessCountBufferPtr = table.CountBuffer.NativePtr;
		_lastBindlessTextureBufferPtr = table.TextureArgumentBuffer.NativePtr;
		_lastBindlessRwTextureBufferPtr = table.RWTextureArgumentBuffer.NativePtr;
		_lastBindlessSamplerBufferPtr = table.SamplerArgumentBuffer.NativePtr;
	}
}
