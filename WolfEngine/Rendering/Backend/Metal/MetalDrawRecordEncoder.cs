using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering.Backend.Metal;

[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
internal sealed class MetalDrawRecordEncoder : IDrawRecordEncoder
{
	public void ResetCommand(IGfxIndirectCommandBuffer commandBuffer, uint commandIndex)
	{
		if (commandBuffer is not MetalIndirectCommandBuffer metalCommandBuffer)
		{
			throw new InvalidOperationException("Indirect command buffer was not created by the Metal backend.");
		}

		metalCommandBuffer.ResetCommand(commandIndex);
	}

	public bool TryEncodeIndexedDrawCommand(
		IGfxDevice device,
		IGfxPipeline pipeline,
		IGfxIndirectCommandBuffer commandBuffer,
		uint commandIndex,
		uint drawArgsCommandIndex,
		Mesh mesh,
		in SharedDrawIndirectEncodeResources resources,
		GraphicsPassBindingSet passBindings,
		in SharedDrawPerDrawBindings perDrawBindings)
	{
		if (device is not MetalDevice || device.GlobalTable is not MetalDescriptorTable descriptorTable)
		{
			throw new InvalidOperationException("Draw records can only be encoded against the Metal device and its bindless table.");
		}

		if (pipeline is not MetalPipeline metalPipeline)
		{
			throw new InvalidOperationException("Pipeline was not created by the Metal backend.");
		}

		// The record captures the argument buffers themselves, so they have to be sized and encoded for the
		// pipeline that executes it before their addresses are taken below.
		descriptorTable.PrepareArgumentBuffersFor(metalPipeline);

		if (commandBuffer is not MetalIndirectCommandBuffer indirectCommands ||
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

		if (descriptorTable.CountBuffer.NativePtr == IntPtr.Zero ||
		    descriptorTable.TextureArgumentBuffer.NativePtr == IntPtr.Zero ||
		    descriptorTable.SamplerArgumentBuffer.NativePtr == IntPtr.Zero)
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
			mesh.GetPackedIndexOffsetBytes(resources.IndexStream),
			0,
			resources.DrawArgsBaseOffsetBytes + (drawArgsCommandIndex * (ulong)Marshal.SizeOf<GpuDrawArgs>()),
			instanceBuffer,
			materialBuffer,
			materialGenerationBuffer,
			drawArgsBuffer,
			passBindings,
			perDrawBindings,
			descriptorTable.CountBuffer,
			descriptorTable.TextureArgumentBuffer,
			descriptorTable.RWTextureArgumentBuffer,
			descriptorTable.SamplerArgumentBuffer);
		return true;
	}
}
