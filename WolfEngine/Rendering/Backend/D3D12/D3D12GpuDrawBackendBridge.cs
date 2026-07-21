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
