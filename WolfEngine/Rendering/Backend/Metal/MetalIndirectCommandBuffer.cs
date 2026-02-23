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
	private readonly Dictionary<nint, BufferRefEntry> _bufferRefsByPointer = new();
	private readonly List<MTLBuffer> _referencedBuffers = new();

	private readonly struct BufferRefEntry
	{
		public BufferRefEntry(MTLBuffer buffer, int refCount, int index)
		{
			Buffer = buffer;
			RefCount = refCount;
			Index = index;
		}

		public MTLBuffer Buffer { get; }
		public int RefCount { get; }
		public int Index { get; }
	}

		private readonly struct CommandBufferReferences
		{
			public CommandBufferReferences(
				MTLBuffer vertexBuffer,
				MTLBuffer indexBuffer,
				MTLBuffer cameraBuffer,
				MTLBuffer shadowCameraBuffer,
				MTLBuffer transparentEnvironmentBuffer,
				MTLBuffer transparentLightingBuffer,
				MTLBuffer instanceBuffer,
				MTLBuffer materialBuffer,
				MTLBuffer materialGenerationBuffer,
				MTLBuffer drawArgsBuffer,
				MTLBuffer bindlessCountBuffer,
				MTLBuffer bindlessTextureBuffer,
				MTLBuffer bindlessRwTextureBuffer,
				MTLBuffer bindlessSamplerBuffer)
		{
			VertexBuffer = vertexBuffer;
			IndexBuffer = indexBuffer;
				CameraBuffer = cameraBuffer;
				ShadowCameraBuffer = shadowCameraBuffer;
				TransparentEnvironmentBuffer = transparentEnvironmentBuffer;
				TransparentLightingBuffer = transparentLightingBuffer;
				InstanceBuffer = instanceBuffer;
				MaterialBuffer = materialBuffer;
				MaterialGenerationBuffer = materialGenerationBuffer;
				DrawArgsBuffer = drawArgsBuffer;
				BindlessCountBuffer = bindlessCountBuffer;
				BindlessTextureBuffer = bindlessTextureBuffer;
				BindlessRwTextureBuffer = bindlessRwTextureBuffer;
			BindlessSamplerBuffer = bindlessSamplerBuffer;
		}

		public MTLBuffer VertexBuffer { get; }
		public MTLBuffer IndexBuffer { get; }
			public MTLBuffer CameraBuffer { get; }
			public MTLBuffer ShadowCameraBuffer { get; }
			public MTLBuffer TransparentEnvironmentBuffer { get; }
			public MTLBuffer TransparentLightingBuffer { get; }
			public MTLBuffer InstanceBuffer { get; }
			public MTLBuffer MaterialBuffer { get; }
			public MTLBuffer MaterialGenerationBuffer { get; }
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
		if (_commandReferences.TryGetValue(commandIndex, out var refs))
		{
			RemoveBufferRefs(in refs);
			_commandReferences.Remove(commandIndex);
		}
	}

	public void EncodeIndexedDrawCommand(
		uint commandIndex,
		MetalBuffer vertexBuffer,
		ulong vertexBufferOffsetBytes,
		MetalBuffer indexBuffer,
		IndexFormat indexFormat,
		uint indexCount,
		ulong indexBufferOffsetBytes,
		int baseVertex,
			ulong drawArgsOffsetBytes,
			MetalBuffer cameraBuffer,
			MetalBuffer shadowCameraBuffer,
			MetalBuffer transparentEnvironmentBuffer,
			MetalBuffer transparentLightingBuffer,
			MetalBuffer instanceBuffer,
			MetalBuffer materialBuffer,
			MetalBuffer materialGenerationBuffer,
			MetalBuffer drawArgsBuffer,
			MTLBuffer bindlessCountBuffer,
			MTLBuffer bindlessTextureBuffer,
			MTLBuffer bindlessRwTextureBuffer,
		MTLBuffer bindlessSamplerBuffer)
	{
		ValidateCommandIndex(commandIndex);
		if (_commandReferences.TryGetValue(commandIndex, out var previousRefs))
		{
			RemoveBufferRefs(in previousRefs);
		}

		using var command = Buffer.IndirectRenderCommand(commandIndex);
		command.Reset();
		command.SetVertexBuffer(vertexBuffer.Buffer, vertexBufferOffsetBytes, 0);
			command.SetVertexBuffer(cameraBuffer.Buffer, 0, 2);
			command.SetVertexBuffer(shadowCameraBuffer.Buffer, 0, 14);
			command.SetVertexBuffer(instanceBuffer.Buffer, 0, 10);
			command.SetVertexBuffer(materialBuffer.Buffer, 0, 11);
			command.SetVertexBuffer(materialGenerationBuffer.Buffer, 0, 13);
			command.SetVertexBuffer(drawArgsBuffer.Buffer, drawArgsOffsetBytes, 12);
			command.SetFragmentBuffer(transparentEnvironmentBuffer.Buffer, 0, 0);
			command.SetFragmentBuffer(cameraBuffer.Buffer, 0, 2);
			command.SetFragmentBuffer(shadowCameraBuffer.Buffer, 0, 14);
			command.SetFragmentBuffer(transparentLightingBuffer.Buffer, 0, 3);
			command.SetFragmentBuffer(instanceBuffer.Buffer, 0, 10);
			command.SetFragmentBuffer(materialBuffer.Buffer, 0, 11);
			command.SetFragmentBuffer(materialGenerationBuffer.Buffer, 0, 13);
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
				indexBufferOffsetBytes,
				1,
				baseVertex,
				0);

		_commandReferences[commandIndex] = new CommandBufferReferences(
			vertexBuffer.Buffer,
			indexBuffer.Buffer,
				cameraBuffer.Buffer,
				shadowCameraBuffer.Buffer,
				transparentEnvironmentBuffer.Buffer,
				transparentLightingBuffer.Buffer,
				instanceBuffer.Buffer,
				materialBuffer.Buffer,
				materialGenerationBuffer.Buffer,
				drawArgsBuffer.Buffer,
				bindlessCountBuffer,
				bindlessTextureBuffer,
				bindlessRwTextureBuffer,
			bindlessSamplerBuffer);
		AddBufferRefs(_commandReferences[commandIndex]);
	}

	internal IReadOnlyList<MTLBuffer> GetReferencedBuffers() => _referencedBuffers;

	public void CollectReferencedBuffers(
		uint maxCommandCount,
		List<MTLBuffer> destination,
		HashSet<nint> seenPointers)
	{
		destination.Clear();
		seenPointers.Clear();

		if (maxCommandCount >= Descriptor.MaxCommandCount)
		{
			destination.AddRange(_referencedBuffers);
			return;
		}

		foreach (var (commandIndex, refs) in _commandReferences)
		{
			if (commandIndex >= maxCommandCount)
			{
				continue;
			}

			AddBuffer(refs.VertexBuffer, destination, seenPointers);
			AddBuffer(refs.IndexBuffer, destination, seenPointers);
				AddBuffer(refs.CameraBuffer, destination, seenPointers);
				AddBuffer(refs.ShadowCameraBuffer, destination, seenPointers);
				AddBuffer(refs.TransparentEnvironmentBuffer, destination, seenPointers);
				AddBuffer(refs.TransparentLightingBuffer, destination, seenPointers);
				AddBuffer(refs.InstanceBuffer, destination, seenPointers);
				AddBuffer(refs.MaterialBuffer, destination, seenPointers);
				AddBuffer(refs.MaterialGenerationBuffer, destination, seenPointers);
				AddBuffer(refs.DrawArgsBuffer, destination, seenPointers);
				AddBuffer(refs.BindlessCountBuffer, destination, seenPointers);
				AddBuffer(refs.BindlessTextureBuffer, destination, seenPointers);
			AddBuffer(refs.BindlessRwTextureBuffer, destination, seenPointers);
			AddBuffer(refs.BindlessSamplerBuffer, destination, seenPointers);
		}
	}

	public void Dispose()
	{
		_bufferRefsByPointer.Clear();
		_referencedBuffers.Clear();
		_commandReferences.Clear();
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

	private void AddBufferRefs(in CommandBufferReferences refs)
	{
		AddBufferRef(refs.VertexBuffer);
		AddBufferRef(refs.IndexBuffer);
			AddBufferRef(refs.CameraBuffer);
			AddBufferRef(refs.ShadowCameraBuffer);
			AddBufferRef(refs.TransparentEnvironmentBuffer);
			AddBufferRef(refs.TransparentLightingBuffer);
			AddBufferRef(refs.InstanceBuffer);
			AddBufferRef(refs.MaterialBuffer);
			AddBufferRef(refs.MaterialGenerationBuffer);
			AddBufferRef(refs.DrawArgsBuffer);
			AddBufferRef(refs.BindlessCountBuffer);
			AddBufferRef(refs.BindlessTextureBuffer);
		AddBufferRef(refs.BindlessRwTextureBuffer);
		AddBufferRef(refs.BindlessSamplerBuffer);
	}

	private void RemoveBufferRefs(in CommandBufferReferences refs)
	{
		RemoveBufferRef(refs.VertexBuffer);
		RemoveBufferRef(refs.IndexBuffer);
			RemoveBufferRef(refs.CameraBuffer);
			RemoveBufferRef(refs.ShadowCameraBuffer);
			RemoveBufferRef(refs.TransparentEnvironmentBuffer);
			RemoveBufferRef(refs.TransparentLightingBuffer);
			RemoveBufferRef(refs.InstanceBuffer);
			RemoveBufferRef(refs.MaterialBuffer);
			RemoveBufferRef(refs.MaterialGenerationBuffer);
			RemoveBufferRef(refs.DrawArgsBuffer);
			RemoveBufferRef(refs.BindlessCountBuffer);
			RemoveBufferRef(refs.BindlessTextureBuffer);
		RemoveBufferRef(refs.BindlessRwTextureBuffer);
		RemoveBufferRef(refs.BindlessSamplerBuffer);
	}

	private void AddBufferRef(MTLBuffer buffer)
	{
		if (buffer.NativePtr == IntPtr.Zero)
		{
			return;
		}

		var key = buffer.NativePtr;
		if (_bufferRefsByPointer.TryGetValue(key, out var entry))
		{
			_bufferRefsByPointer[key] = new BufferRefEntry(entry.Buffer, entry.RefCount + 1, entry.Index);
			return;
		}

		var index = _referencedBuffers.Count;
		_referencedBuffers.Add(buffer);
		_bufferRefsByPointer[key] = new BufferRefEntry(buffer, 1, index);
	}

	private void RemoveBufferRef(MTLBuffer buffer)
	{
		if (buffer.NativePtr == IntPtr.Zero)
		{
			return;
		}

		var key = buffer.NativePtr;
		if (_bufferRefsByPointer.TryGetValue(key, out var entry) == false)
		{
			return;
		}

		if (entry.RefCount > 1)
		{
			_bufferRefsByPointer[key] = new BufferRefEntry(entry.Buffer, entry.RefCount - 1, entry.Index);
			return;
		}

		var removeIndex = entry.Index;
		var lastIndex = _referencedBuffers.Count - 1;
		if (removeIndex != lastIndex)
		{
			var movedBuffer = _referencedBuffers[lastIndex];
			_referencedBuffers[removeIndex] = movedBuffer;
			var movedKey = movedBuffer.NativePtr;
			if (_bufferRefsByPointer.TryGetValue(movedKey, out var movedEntry))
			{
				_bufferRefsByPointer[movedKey] = new BufferRefEntry(movedEntry.Buffer, movedEntry.RefCount, removeIndex);
			}
		}

		_referencedBuffers.RemoveAt(lastIndex);
		_bufferRefsByPointer.Remove(key);
	}
}
