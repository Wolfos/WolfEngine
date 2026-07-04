using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

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
			MTLBuffer instanceBuffer,
			MTLBuffer materialBuffer,
			MTLBuffer materialGenerationBuffer,
			MTLBuffer terrainMaterialBuffer,
			MTLBuffer terrainLayerBuffer,
			MTLBuffer drawArgsBuffer,
			MTLBuffer ddgiDebugBuffer,
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
			MaterialGenerationBuffer = materialGenerationBuffer;
			TerrainMaterialBuffer = terrainMaterialBuffer;
			TerrainLayerBuffer = terrainLayerBuffer;
			DrawArgsBuffer = drawArgsBuffer;
			DdgiDebugBuffer = ddgiDebugBuffer;
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
		public MTLBuffer MaterialGenerationBuffer { get; }
		public MTLBuffer TerrainMaterialBuffer { get; }
		public MTLBuffer TerrainLayerBuffer { get; }
		public MTLBuffer DrawArgsBuffer { get; }
		public MTLBuffer DdgiDebugBuffer { get; }
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
		MetalBuffer instanceBuffer,
		MetalBuffer materialBuffer,
		MetalBuffer materialGenerationBuffer,
		MetalBuffer terrainMaterialBuffer,
		MetalBuffer terrainLayerBuffer,
		MetalBuffer drawArgsBuffer,
		MetalBuffer? ddgiDebugBuffer,
		in SharedDrawGraphicsBufferBindings bindings,
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
		command.SetVertexBuffer(cameraBuffer.Buffer, 0, bindings.CameraRegisterIndex);
		command.SetVertexBuffer(instanceBuffer.Buffer, 0, bindings.InstanceRegisterIndex);
		command.SetVertexBuffer(materialBuffer.Buffer, 0, bindings.MaterialRegisterIndex);
		command.SetVertexBuffer(materialGenerationBuffer.Buffer, 0, bindings.MaterialGenerationRegisterIndex);
		if (bindings.TerrainMaterialRegisterIndex is { } terrainMaterialRegisterIndex)
		{
			command.SetVertexBuffer(terrainMaterialBuffer.Buffer, 0, terrainMaterialRegisterIndex);
		}

		if (bindings.TerrainLayerRegisterIndex is { } terrainLayerRegisterIndex)
		{
			command.SetVertexBuffer(terrainLayerBuffer.Buffer, 0, terrainLayerRegisterIndex);
		}

		command.SetVertexBuffer(drawArgsBuffer.Buffer, drawArgsOffsetBytes, bindings.DrawArgsRegisterIndex);
		command.SetFragmentBuffer(cameraBuffer.Buffer, 0, bindings.CameraRegisterIndex);
		command.SetFragmentBuffer(instanceBuffer.Buffer, 0, bindings.InstanceRegisterIndex);
		command.SetFragmentBuffer(materialBuffer.Buffer, 0, bindings.MaterialRegisterIndex);
		command.SetFragmentBuffer(materialGenerationBuffer.Buffer, 0, bindings.MaterialGenerationRegisterIndex);
		if (bindings.TerrainMaterialRegisterIndex is { } fragmentTerrainMaterialRegisterIndex)
		{
			command.SetFragmentBuffer(terrainMaterialBuffer.Buffer, 0, fragmentTerrainMaterialRegisterIndex);
		}

		if (bindings.TerrainLayerRegisterIndex is { } fragmentTerrainLayerRegisterIndex)
		{
			command.SetFragmentBuffer(terrainLayerBuffer.Buffer, 0, fragmentTerrainLayerRegisterIndex);
		}

		command.SetFragmentBuffer(drawArgsBuffer.Buffer, drawArgsOffsetBytes, bindings.DrawArgsRegisterIndex);
		var ddgiDebugNativeBuffer = default(MTLBuffer);
		if (ddgiDebugBuffer is not null && bindings.DdgiDebugRegisterIndex is { } ddgiDebugRegisterIndex)
		{
			ddgiDebugNativeBuffer = ddgiDebugBuffer.Buffer;
			command.SetVertexBuffer(ddgiDebugNativeBuffer, 0, ddgiDebugRegisterIndex);
			command.SetFragmentBuffer(ddgiDebugNativeBuffer, 0, ddgiDebugRegisterIndex);
		}

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
			instanceBuffer.Buffer,
			materialBuffer.Buffer,
			materialGenerationBuffer.Buffer,
			terrainMaterialBuffer.Buffer,
			terrainLayerBuffer.Buffer,
			drawArgsBuffer.Buffer,
			ddgiDebugNativeBuffer,
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
			AddBuffer(refs.InstanceBuffer, destination, seenPointers);
			AddBuffer(refs.MaterialBuffer, destination, seenPointers);
			AddBuffer(refs.MaterialGenerationBuffer, destination, seenPointers);
			AddBuffer(refs.TerrainMaterialBuffer, destination, seenPointers);
			AddBuffer(refs.TerrainLayerBuffer, destination, seenPointers);
			AddBuffer(refs.DrawArgsBuffer, destination, seenPointers);
			AddBuffer(refs.DdgiDebugBuffer, destination, seenPointers);
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
		AddBufferRef(refs.InstanceBuffer);
		AddBufferRef(refs.MaterialBuffer);
		AddBufferRef(refs.MaterialGenerationBuffer);
		AddBufferRef(refs.TerrainMaterialBuffer);
		AddBufferRef(refs.TerrainLayerBuffer);
		AddBufferRef(refs.DrawArgsBuffer);
		AddBufferRef(refs.DdgiDebugBuffer);
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
		RemoveBufferRef(refs.InstanceBuffer);
		RemoveBufferRef(refs.MaterialBuffer);
		RemoveBufferRef(refs.MaterialGenerationBuffer);
		RemoveBufferRef(refs.TerrainMaterialBuffer);
		RemoveBufferRef(refs.TerrainLayerBuffer);
		RemoveBufferRef(refs.DrawArgsBuffer);
		RemoveBufferRef(refs.DdgiDebugBuffer);
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
