using System.Collections.Generic;
using System.Runtime.Versioning;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.Metal;

[SupportedOSPlatform("macos")]
internal sealed class MetalIndirectCommandBuffer : IGfxIndirectCommandBuffer, IDisposable
{
	private readonly Dictionary<uint, CommandBufferReferences> _commandReferences = new();

	private readonly struct CommandBufferReferences
	{
		public CommandBufferReferences(
			MTLBuffer vertexBuffer,
			MTLBuffer indexBuffer,
			MTLBuffer cameraBuffer,
			MTLBuffer instanceBuffer,
			MTLBuffer materialBuffer,
			MTLBuffer drawArgsBuffer,
			MTLBuffer bindlessCountBuffer,
			MTLBuffer bindlessTextureBuffer,
			MTLBuffer bindlessRwTextureBuffer,
			MTLBuffer bindlessSamplerBuffer)
		{
			VertexBuffer = vertexBuffer;
			IndexBuffer = indexBuffer;
			CameraBuffer = cameraBuffer;
			InstanceBuffer = instanceBuffer;
			MaterialBuffer = materialBuffer;
			DrawArgsBuffer = drawArgsBuffer;
			BindlessCountBuffer = bindlessCountBuffer;
			BindlessTextureBuffer = bindlessTextureBuffer;
			BindlessRwTextureBuffer = bindlessRwTextureBuffer;
			BindlessSamplerBuffer = bindlessSamplerBuffer;
		}

		public MTLBuffer VertexBuffer { get; }
		public MTLBuffer IndexBuffer { get; }
		public MTLBuffer CameraBuffer { get; }
		public MTLBuffer InstanceBuffer { get; }
		public MTLBuffer MaterialBuffer { get; }
		public MTLBuffer DrawArgsBuffer { get; }
		public MTLBuffer BindlessCountBuffer { get; }
		public MTLBuffer BindlessTextureBuffer { get; }
		public MTLBuffer BindlessRwTextureBuffer { get; }
		public MTLBuffer BindlessSamplerBuffer { get; }
	}

	public MetalIndirectCommandBuffer(string? name, in IndirectCommandBufferDescriptor descriptor, MTLIndirectCommandBuffer buffer)
	{
		Name = name;
		Descriptor = descriptor;
		Buffer = buffer;
	}

	public string? Name { get; }

	public IndirectCommandBufferDescriptor Descriptor { get; }

	internal MTLIndirectCommandBuffer Buffer { get; }

	public void ResetCommand(uint commandIndex)
	{
		ValidateCommandIndex(commandIndex);
		Buffer.Reset(new NSRange { location = commandIndex, length = 1 });
		_commandReferences.Remove(commandIndex);
	}

	public void EncodeIndexedDrawCommand(
		uint commandIndex,
		MetalBuffer vertexBuffer,
		MetalBuffer indexBuffer,
		IndexFormat indexFormat,
		uint indexCount,
		int baseVertex,
		ulong drawArgsOffsetBytes,
		MetalBuffer cameraBuffer,
		MetalBuffer instanceBuffer,
		MetalBuffer materialBuffer,
		MetalBuffer drawArgsBuffer,
		MTLBuffer bindlessCountBuffer,
		MTLBuffer bindlessTextureBuffer,
		MTLBuffer bindlessRwTextureBuffer,
		MTLBuffer bindlessSamplerBuffer)
	{
		ValidateCommandIndex(commandIndex);

		using var command = Buffer.IndirectRenderCommand(commandIndex);
		command.Reset();
		command.SetVertexBuffer(vertexBuffer.Buffer, 0, 0);
		command.SetVertexBuffer(cameraBuffer.Buffer, 0, 2);
		command.SetVertexBuffer(instanceBuffer.Buffer, 0, 10);
		command.SetVertexBuffer(materialBuffer.Buffer, 0, 11);
		command.SetVertexBuffer(drawArgsBuffer.Buffer, drawArgsOffsetBytes, 12);
		command.SetFragmentBuffer(cameraBuffer.Buffer, 0, 2);
		command.SetFragmentBuffer(instanceBuffer.Buffer, 0, 10);
		command.SetFragmentBuffer(materialBuffer.Buffer, 0, 11);
		command.SetFragmentBuffer(drawArgsBuffer.Buffer, drawArgsOffsetBytes, 12);

		if (bindlessCountBuffer.NativePtr != IntPtr.Zero)
		{
			command.SetVertexBuffer(bindlessCountBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexCounts);
			command.SetFragmentBuffer(bindlessCountBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexCounts);
		}

		if (bindlessTextureBuffer.NativePtr != IntPtr.Zero)
		{
			command.SetVertexBuffer(bindlessTextureBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexTextures);
			command.SetFragmentBuffer(bindlessTextureBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexTextures);
		}

		if (bindlessRwTextureBuffer.NativePtr != IntPtr.Zero)
		{
			command.SetFragmentBuffer(bindlessRwTextureBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexRWTextures);
		}

		if (bindlessSamplerBuffer.NativePtr != IntPtr.Zero)
		{
			command.SetVertexBuffer(bindlessSamplerBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexSamplers);
			command.SetFragmentBuffer(bindlessSamplerBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexSamplers);
		}

		var metalIndexType = indexFormat == IndexFormat.UInt16
			? MTLIndexType.UInt16
			: MTLIndexType.UInt32;
			command.DrawIndexedPrimitives(
				MTLPrimitiveType.Triangle,
				indexCount,
				metalIndexType,
				indexBuffer.Buffer,
				0,
				1,
				baseVertex,
				0);

		_commandReferences[commandIndex] = new CommandBufferReferences(
			vertexBuffer.Buffer,
			indexBuffer.Buffer,
			cameraBuffer.Buffer,
			instanceBuffer.Buffer,
			materialBuffer.Buffer,
			drawArgsBuffer.Buffer,
			bindlessCountBuffer,
			bindlessTextureBuffer,
			bindlessRwTextureBuffer,
			bindlessSamplerBuffer);
	}

	public void CollectReferencedBuffers(
		uint maxCommandCount,
		List<MTLBuffer> destination,
		HashSet<nint> seenPointers)
	{
		destination.Clear();
		seenPointers.Clear();

		foreach (var (commandIndex, refs) in _commandReferences)
		{
			if (commandIndex >= maxCommandCount)
			{
				continue;
			}

			AddBuffer(refs.VertexBuffer, destination, seenPointers);
			AddBuffer(refs.IndexBuffer, destination, seenPointers);
			AddBuffer(refs.CameraBuffer, destination, seenPointers);
			AddBuffer(refs.InstanceBuffer, destination, seenPointers);
			AddBuffer(refs.MaterialBuffer, destination, seenPointers);
			AddBuffer(refs.DrawArgsBuffer, destination, seenPointers);
			AddBuffer(refs.BindlessCountBuffer, destination, seenPointers);
			AddBuffer(refs.BindlessTextureBuffer, destination, seenPointers);
			AddBuffer(refs.BindlessRwTextureBuffer, destination, seenPointers);
			AddBuffer(refs.BindlessSamplerBuffer, destination, seenPointers);
		}
	}

	public void Dispose()
	{
		if (Buffer.NativePtr != IntPtr.Zero)
		{
			Buffer.Dispose();
		}
	}

	private void ValidateCommandIndex(uint commandIndex)
	{
		if (commandIndex >= Descriptor.MaxCommandCount)
		{
			throw new ArgumentOutOfRangeException(nameof(commandIndex), commandIndex, "Command index is out of range.");
		}
	}

	private static void AddBuffer(MTLBuffer buffer, List<MTLBuffer> destination, HashSet<nint> seenPointers)
	{
		if (buffer.NativePtr == IntPtr.Zero)
		{
			return;
		}

		if (seenPointers.Add(buffer.NativePtr))
		{
			destination.Add(buffer);
		}
	}
}
