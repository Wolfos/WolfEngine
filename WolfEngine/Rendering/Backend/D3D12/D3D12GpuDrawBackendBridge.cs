#nullable enable

using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering.Backend.D3D12;

public sealed class D3D12GpuDrawBackendBridge : IGpuDrawBackendBridge
{
	private static NotImplementedException CreateNotImplementedException() =>
		new("GpuDraw backend bridge is not implemented for D3D12.");

	public GpuDrawBackendFrameSignals PrepareFrame(
		IGfxDevice device,
		IRenderer renderer,
		GpuDrawResources resources,
		IGfxPipeline? primaryGBufferPipeline)
	{
		throw CreateNotImplementedException();
	}

	public bool TryGetSlotIndirectCommands(
		GpuDrawResources resources,
		int slotIndex,
		out IGfxIndirectCommandBuffer[] commandBuffers)
	{
		throw CreateNotImplementedException();
	}

	public void ResetCommand(IGfxIndirectCommandBuffer commandBuffer, uint commandIndex)
	{
		throw CreateNotImplementedException();
	}

	public bool TryEncodeIndexedDrawCommand(
		IGfxIndirectCommandBuffer commandBuffer,
		uint commandIndex,
		Mesh mesh,
		GpuDrawResources resources)
	{
		throw CreateNotImplementedException();
	}

	public void SampleGpuDiagnosticCounters(
		IGfxBuffer? diagnosticsCounterBuffer,
		uint[] lastCounters,
		GpuDrawHardeningStats stats)
	{
		throw CreateNotImplementedException();
	}
}
