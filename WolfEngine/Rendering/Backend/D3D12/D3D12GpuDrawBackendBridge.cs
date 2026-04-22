#nullable enable

using System;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering.Backend.D3D12;

public sealed class D3D12GpuDrawBackendBridge : IGpuDrawBackendBridge
{
	public GpuDrawBackendFrameSignals PrepareFrame(
		IGfxDevice device,
		IRenderer renderer,
		GpuDrawResources resources,
		IGfxPipeline? primaryGBufferPipeline)
	{
		if (device is not D3D12Device)
		{
			return new GpuDrawBackendFrameSignals(
				requiresFullSlotReencode: false,
				supportsIndirectStructuralUpdates: false);
		}

		_ = renderer;
		_ = resources;
		_ = primaryGBufferPipeline;
		return new GpuDrawBackendFrameSignals(
			requiresFullSlotReencode: false,
			supportsIndirectStructuralUpdates: true);
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
			if (resources.GetIndirectCommandBufferSlot(slotIndex, i) is not D3D12IndirectCommandBuffer commandBuffer)
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
		if (commandBuffer is not D3D12IndirectCommandBuffer d3d12CommandBuffer)
		{
			throw new InvalidOperationException("Indirect command buffer was not created by the Direct3D12 backend.");
		}

		d3d12CommandBuffer.ResetCommand(commandIndex);
	}

	public bool TryEncodeIndexedDrawCommand(
		IGfxIndirectCommandBuffer commandBuffer,
		uint commandIndex,
		Mesh mesh,
		GpuDrawResources resources,
		in SharedDrawGraphicsBufferBindings bindings)
	{
		_ = bindings;
		if (commandBuffer is not D3D12IndirectCommandBuffer d3d12CommandBuffer ||
		    resources.InstanceBuffer is not D3D12Buffer instanceBuffer ||
		    resources.MaterialBuffer is not D3D12Buffer materialBuffer ||
		    resources.DrawArgsBuffer is not D3D12Buffer drawArgsBuffer ||
		    resources.MaterialGenerationBuffer is not D3D12Buffer materialGenerationBuffer ||
		    resources.CameraBuffer is not D3D12Buffer cameraBuffer ||
		    resources.ShadowCameraBuffer is not D3D12Buffer shadowCameraBuffer ||
		    resources.TransparentEnvironmentBuffer is not D3D12Buffer transparentEnvironmentBuffer ||
		    resources.TransparentLightingBuffer is not D3D12Buffer transparentLightingBuffer)
		{
			return false;
		}

		d3d12CommandBuffer.EncodeIndexedDrawCommand(
			commandIndex,
			mesh,
			instanceBuffer,
			materialBuffer,
			drawArgsBuffer,
			materialGenerationBuffer,
			cameraBuffer,
			shadowCameraBuffer,
			transparentEnvironmentBuffer,
			transparentLightingBuffer);
		return true;
	}

	public void SampleVisibilityDiagnostics(
		IGfxBuffer? drawCountPerBucketBuffer,
		IGfxBuffer? drawExecutionRangePerBucketBuffer,
		GpuDrawHardeningStats stats)
	{
		_ = drawCountPerBucketBuffer;
		_ = drawExecutionRangePerBucketBuffer;
		_ = stats;
	}

	public void SampleGpuDiagnosticCounters(
		IGfxBuffer? diagnosticsCounterBuffer,
		uint[] lastCounters,
		GpuDrawHardeningStats stats)
	{
		_ = diagnosticsCounterBuffer;
		_ = lastCounters;
		_ = stats;
	}
}
