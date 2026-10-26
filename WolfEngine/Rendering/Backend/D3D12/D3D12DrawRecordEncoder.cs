using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering.Backend.D3D12;

public sealed class D3D12DrawRecordEncoder : IDrawRecordEncoder
{
	/// <summary>
	/// Records hold only a draw index and draw arguments, so neither the pipeline nor the descriptor heap is
	/// captured, and there is nothing to make current before encoding.
	/// </summary>
	public void PrepareForPipeline(IGfxDevice device, IGfxPipeline pipeline)
	{
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
		IGfxDevice device,
		IGfxIndirectCommandBuffer commandBuffer,
		uint commandIndex,
		uint drawArgsCommandIndex,
		Mesh mesh,
		in SharedDrawIndirectEncodeResources resources,
		GraphicsPassBindingSet passBindings,
		in SharedDrawPerDrawBindings perDrawBindings)
	{
		if (device is not D3D12Device)
		{
			throw new InvalidOperationException("Draw records can only be encoded against the Direct3D12 device.");
		}

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
			perDrawBindings,
			resources.IndexStream);
		return true;
	}
}
