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

		return new GpuDrawBackendFrameSignals(requiresFullSlotReencode, supportsIndirectStructuralUpdates: true);
	}

	public bool TryGetSlotIndirectCommands(
		GpuDrawResources resources,
		int slotIndex,
		out IGfxIndirectCommandBuffer[] commandBuffers)
	{
		commandBuffers = Array.Empty<IGfxIndirectCommandBuffer>();
		var executionLaneCount = GpuDrawExecutionLanes.ExecutionLaneCount;
		if (executionLaneCount <= 0)
		{
			return false;
		}

		var resolved = new IGfxIndirectCommandBuffer[executionLaneCount];
		for (var i = 0; i < executionLaneCount; i++)
		{
			if (resources.GetIndirectCommandBufferSlot(slotIndex, i) is not MetalIndirectCommandBuffer commandBuffer)
			{
				return false;
			}

			resolved[i] = commandBuffer;
		}

		commandBuffers = resolved;
		return true;
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
		Mesh mesh,
		GpuDrawResources resources,
		in SharedDrawGraphicsBufferBindings bindings)
	{
		if (_descriptorTable is null ||
		    commandBuffer is not MetalIndirectCommandBuffer indirectCommands ||
		    mesh.VertexBuffer is not MetalBuffer metalVertexBuffer ||
		    mesh.IndexBuffer is not MetalBuffer metalIndexBuffer ||
		    resources.CameraBuffer is not MetalBuffer cameraBuffer ||
		    resources.ShadowCameraBuffer is not MetalBuffer shadowCameraBuffer ||
		    resources.TransparentEnvironmentBuffer is not MetalBuffer transparentEnvironmentBuffer ||
		    resources.TransparentLightingBuffer is not MetalBuffer transparentLightingBuffer ||
		    resources.InstanceBuffer is not MetalBuffer instanceBuffer ||
		    resources.MaterialBuffer is not MetalBuffer materialBuffer ||
		    resources.DrawArgsBuffer is not MetalBuffer drawArgsBuffer ||
		    resources.MaterialGenerationBuffer is not MetalBuffer materialGenerationBuffer)
		{
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
			commandIndex * (ulong)Marshal.SizeOf<GpuDrawArgs>(),
			cameraBuffer,
			shadowCameraBuffer,
			transparentEnvironmentBuffer,
			transparentLightingBuffer,
			instanceBuffer,
			materialBuffer,
			materialGenerationBuffer,
			drawArgsBuffer,
			bindings,
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

		var definitions = GBufferDrawBuckets.StableOrderDefinitions;
		var visibleCounts = new ReadOnlySpan<uint>(
			(void*)drawCountBuffer.Buffer.Contents.ToPointer(),
			GpuDrawExecutionLanes.ExecutionLaneCount);
		var executionRanges = new ReadOnlySpan<uint>(
			(void*)executionRangeBuffer.Buffer.Contents.ToPointer(),
			GpuDrawExecutionLanes.ExecutionLaneCount * 2);
		for (var i = 0; i < definitions.Length; i++)
		{
			var definition = definitions[i];
			long visibleCount = 0;
			var rangeStart = 0u;
			var rangeEnd = 0u;
			var hasRange = false;
			var laneDefinitions = GpuDrawExecutionLanes.Definitions;
			for (var laneIndex = 0; laneIndex < laneDefinitions.Length; laneIndex++)
			{
				var lane = laneDefinitions[laneIndex];
				if (lane.BucketId != definition.BucketId)
				{
					continue;
				}

				visibleCount += visibleCounts[lane.ExecutionIndex];
				var candidateStart = executionRanges[(lane.ExecutionIndex * 2) + 0];
				var candidateEnd = executionRanges[(lane.ExecutionIndex * 2) + 1];
				if (candidateEnd == 0)
				{
					continue;
				}

				if (hasRange == false)
				{
					rangeStart = candidateStart;
					rangeEnd = candidateEnd;
					hasRange = true;
					continue;
				}

				rangeStart = Math.Min(rangeStart, candidateStart);
				rangeEnd = Math.Max(rangeEnd, candidateEnd);
			}

			stats.SetVisibleDrawCount(definition.BucketId, visibleCount);
			stats.SetExecutionRange(definition.BucketId, hasRange ? rangeStart : 0, hasRange ? rangeEnd : 0);
		}
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
		for (var i = 0; i < counters.Length; i++)
		{
			var current = counters[i];
			var previous = lastCounters[i];
			if (current < previous)
			{
				lastCounters[i] = current;
				continue;
			}

			var delta = current - previous;
			if (delta == 0)
			{
				continue;
			}

			lastCounters[i] = current;
			switch (i)
			{
				case 0:
					stats.AddStaleHandleRejects(delta);
					break;
				case 1:
					stats.AddFallbackProxySubstitutions(delta);
					break;
				case 4:
					stats.AddVisibleListClampHits(delta);
					break;
				case 5:
					stats.AddMaterialFallbackDrawHits(delta);
					break;
			}
		}
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
