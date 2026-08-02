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
		public BufferRefEntry(MTLBuffer buffer, MetalBuffer? owner, int refCount, int index)
		{
			Buffer = buffer;
			Owner = owner;
			RefCount = refCount;
			Index = index;
		}

		public MTLBuffer Buffer { get; }
		public MetalBuffer? Owner { get; }
		public int RefCount { get; }
		public int Index { get; }
	}

	private readonly struct CommandBufferReferences
	{
		public CommandBufferReferences(
			MTLBuffer vertexBuffer,
			MTLBuffer indexBuffer,
			MTLBuffer instanceBuffer,
			MTLBuffer materialBuffer,
			MTLBuffer materialGenerationBuffer,
			MTLBuffer drawArgsBuffer,
			MTLBuffer[] passBuffers,
			MetalBuffer vertexBufferOwner,
			MetalBuffer indexBufferOwner,
			MetalBuffer instanceBufferOwner,
			MetalBuffer materialBufferOwner,
			MetalBuffer materialGenerationBufferOwner,
			MetalBuffer drawArgsBufferOwner,
			MetalBuffer[] passBufferOwners,
			MTLBuffer bindlessCountBuffer,
			MTLBuffer bindlessTextureBuffer,
			MTLBuffer bindlessRwTextureBuffer,
			MTLBuffer bindlessSamplerBuffer)
		{
			VertexBuffer = vertexBuffer;
			IndexBuffer = indexBuffer;
			InstanceBuffer = instanceBuffer;
			MaterialBuffer = materialBuffer;
			MaterialGenerationBuffer = materialGenerationBuffer;
			DrawArgsBuffer = drawArgsBuffer;
			PassBuffers = passBuffers;
			VertexBufferOwner = vertexBufferOwner;
			IndexBufferOwner = indexBufferOwner;
			InstanceBufferOwner = instanceBufferOwner;
			MaterialBufferOwner = materialBufferOwner;
			MaterialGenerationBufferOwner = materialGenerationBufferOwner;
			DrawArgsBufferOwner = drawArgsBufferOwner;
			PassBufferOwners = passBufferOwners;
			BindlessCountBuffer = bindlessCountBuffer;
			BindlessTextureBuffer = bindlessTextureBuffer;
			BindlessRwTextureBuffer = bindlessRwTextureBuffer;
			BindlessSamplerBuffer = bindlessSamplerBuffer;
		}

		public MTLBuffer VertexBuffer { get; }
		public MTLBuffer IndexBuffer { get; }
		public MTLBuffer InstanceBuffer { get; }
		public MTLBuffer MaterialBuffer { get; }
		public MTLBuffer MaterialGenerationBuffer { get; }
		public MTLBuffer DrawArgsBuffer { get; }
		public MTLBuffer[] PassBuffers { get; }
		public MetalBuffer VertexBufferOwner { get; }
		public MetalBuffer IndexBufferOwner { get; }
		public MetalBuffer InstanceBufferOwner { get; }
		public MetalBuffer MaterialBufferOwner { get; }
		public MetalBuffer MaterialGenerationBufferOwner { get; }
		public MetalBuffer DrawArgsBufferOwner { get; }
		public MetalBuffer[] PassBufferOwners { get; }
		public MTLBuffer BindlessCountBuffer { get; }
		public MTLBuffer BindlessTextureBuffer { get; }
		public MTLBuffer BindlessRwTextureBuffer { get; }
		public MTLBuffer BindlessSamplerBuffer { get; }
	}

	public MetalIndirectCommandBuffer(
		string? name,
		in IndirectCommandBufferDescriptor descriptor,
		MTLIndirectCommandBuffer buffer,
		MTLIndirectCommandBuffer compactedBuffer = default,
		MTLBuffer compactionArguments = default)
	{
		Name = name;
		Descriptor = descriptor;
		Buffer = buffer;
		CompactedBuffer = compactedBuffer;
		CompactionArguments = compactionArguments;
	}

	public string? Name { get; }

	public IndirectCommandBufferDescriptor Descriptor { get; }

	internal MTLIndirectCommandBuffer Buffer { get; }

	/// <summary>
	/// Destination for the commands culling leaves visible, holding them densely from index zero so the
	/// execute walks only what survived. Null-valued when the compaction kernel is unavailable.
	/// </summary>
	internal MTLIndirectCommandBuffer CompactedBuffer { get; }

	/// <summary>
	/// Argument buffer naming <see cref="Buffer"/> and <see cref="CompactedBuffer"/> to the compaction
	/// kernel. Metal cannot bind an indirect command buffer to a compute encoder any other way, and the
	/// pair never changes, so it is built once with the page.
	/// </summary>
	internal MTLBuffer CompactionArguments { get; }

	public IndirectCompactionKind CompactionKind =>
		CompactedBuffer.NativePtr != IntPtr.Zero && CompactionArguments.NativePtr != IntPtr.Zero
			? IndirectCompactionKind.NativeCommands
			: IndirectCompactionKind.None;

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
		MetalBuffer instanceBuffer,
		MetalBuffer materialBuffer,
		MetalBuffer materialGenerationBuffer,
		MetalBuffer drawArgsBuffer,
		GraphicsPassBindingSet passBindings,
		in SharedDrawPerDrawBindings perDrawBindings,
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
		command.SetVertexBuffer(instanceBuffer.Buffer, 0, perDrawBindings.InstanceRegisterIndex);
		command.SetVertexBuffer(materialBuffer.Buffer, 0, perDrawBindings.MaterialRegisterIndex);
		command.SetVertexBuffer(materialGenerationBuffer.Buffer, 0, perDrawBindings.MaterialGenerationRegisterIndex);
		command.SetVertexBuffer(drawArgsBuffer.Buffer, drawArgsOffsetBytes, perDrawBindings.DrawArgsRegisterIndex);
		command.SetFragmentBuffer(instanceBuffer.Buffer, 0, perDrawBindings.InstanceRegisterIndex);
		command.SetFragmentBuffer(materialBuffer.Buffer, 0, perDrawBindings.MaterialRegisterIndex);
		command.SetFragmentBuffer(materialGenerationBuffer.Buffer, 0, perDrawBindings.MaterialGenerationRegisterIndex);
		command.SetFragmentBuffer(drawArgsBuffer.Buffer, drawArgsOffsetBytes, perDrawBindings.DrawArgsRegisterIndex);
		var passBuffers = new MTLBuffer[passBindings.Bindings.Length];
		var passBufferOwners = new MetalBuffer[passBindings.Bindings.Length];
		for (var i = 0; i < passBindings.Bindings.Length; i++)
		{
			var binding = passBindings.Bindings[i];
			var owner = (MetalBuffer)binding.Resource;
			var buffer = owner.Buffer;
			passBuffers[i] = buffer;
			passBufferOwners[i] = owner;
			// Shared-draw material pipelines reserve vertex buffer slot zero for
			// the mesh stream.  A fragment-scoped b0 (for example transparent
			// environment parameters) must never replace it in an ICB command.
			if (binding.RegisterIndex != 0 &&
				binding.Visibility is GraphicsPassBindingVisibility.Vertex or GraphicsPassBindingVisibility.All)
				command.SetVertexBuffer(buffer, 0, binding.RegisterIndex);
			if (binding.Visibility is GraphicsPassBindingVisibility.Fragment or GraphicsPassBindingVisibility.All)
				command.SetFragmentBuffer(buffer, 0, binding.RegisterIndex);
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
			instanceBuffer.Buffer,
			materialBuffer.Buffer,
			materialGenerationBuffer.Buffer,
			drawArgsBuffer.Buffer,
			passBuffers,
			vertexBuffer,
			indexBuffer,
			instanceBuffer,
			materialBuffer,
			materialGenerationBuffer,
			drawArgsBuffer,
			passBufferOwners,
			bindlessCountBuffer,
			bindlessTextureBuffer,
			bindlessRwTextureBuffer,
			bindlessSamplerBuffer);
		AddBufferRefs(_commandReferences[commandIndex]);
	}

	internal IReadOnlyList<MTLBuffer> GetReferencedBuffers() => _referencedBuffers;

	internal bool UsesCurrentBindlessBuffers(MetalDescriptorTable descriptorTable, uint maxCommandCount)
	{
		var currentCountBuffer = descriptorTable.CountBuffer.NativePtr;
		var currentTextureBuffer = descriptorTable.TextureArgumentBuffer.NativePtr;
		var currentRwTextureBuffer = descriptorTable.RWTextureArgumentBuffer.NativePtr;
		var currentSamplerBuffer = descriptorTable.SamplerArgumentBuffer.NativePtr;
		foreach (var (commandIndex, references) in _commandReferences)
		{
			if (commandIndex >= maxCommandCount)
			{
				continue;
			}

			if (references.BindlessCountBuffer.NativePtr != currentCountBuffer ||
			    references.BindlessTextureBuffer.NativePtr != currentTextureBuffer ||
			    references.BindlessRwTextureBuffer.NativePtr != currentRwTextureBuffer ||
			    references.BindlessSamplerBuffer.NativePtr != currentSamplerBuffer)
			{
				return false;
			}
		}

		return true;
	}

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
			AddBuffer(refs.InstanceBuffer, destination, seenPointers);
			AddBuffer(refs.MaterialBuffer, destination, seenPointers);
			AddBuffer(refs.MaterialGenerationBuffer, destination, seenPointers);
			AddBuffer(refs.DrawArgsBuffer, destination, seenPointers);
			foreach (var passBuffer in refs.PassBuffers)
				AddBuffer(passBuffer, destination, seenPointers);
			AddBuffer(refs.BindlessCountBuffer, destination, seenPointers);
			AddBuffer(refs.BindlessTextureBuffer, destination, seenPointers);
			AddBuffer(refs.BindlessRwTextureBuffer, destination, seenPointers);
			AddBuffer(refs.BindlessSamplerBuffer, destination, seenPointers);
		}
	}

	public void Dispose()
	{
		foreach (var entry in _bufferRefsByPointer.Values)
		{
			entry.Owner?.ReleaseFromIndirectUse();
		}
		_bufferRefsByPointer.Clear();
		_referencedBuffers.Clear();
		_commandReferences.Clear();
		if (Buffer.NativePtr != IntPtr.Zero)
		{
			Buffer.Dispose();
		}

		if (CompactedBuffer.NativePtr != IntPtr.Zero)
		{
			CompactedBuffer.Dispose();
		}

		if (CompactionArguments.NativePtr != IntPtr.Zero)
		{
			CompactionArguments.Dispose();
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
		AddBufferRef(refs.VertexBuffer, refs.VertexBufferOwner);
		AddBufferRef(refs.IndexBuffer, refs.IndexBufferOwner);
		AddBufferRef(refs.InstanceBuffer, refs.InstanceBufferOwner);
		AddBufferRef(refs.MaterialBuffer, refs.MaterialBufferOwner);
		AddBufferRef(refs.MaterialGenerationBuffer, refs.MaterialGenerationBufferOwner);
		AddBufferRef(refs.DrawArgsBuffer, refs.DrawArgsBufferOwner);
		for (var i = 0; i < refs.PassBuffers.Length; i++)
			AddBufferRef(refs.PassBuffers[i], refs.PassBufferOwners[i]);
		AddBufferRef(refs.BindlessCountBuffer);
		AddBufferRef(refs.BindlessTextureBuffer);
		AddBufferRef(refs.BindlessRwTextureBuffer);
		AddBufferRef(refs.BindlessSamplerBuffer);
	}

	private void RemoveBufferRefs(in CommandBufferReferences refs)
	{
		RemoveBufferRef(refs.VertexBuffer);
		RemoveBufferRef(refs.IndexBuffer);
		RemoveBufferRef(refs.InstanceBuffer);
		RemoveBufferRef(refs.MaterialBuffer);
		RemoveBufferRef(refs.MaterialGenerationBuffer);
		RemoveBufferRef(refs.DrawArgsBuffer);
		foreach (var passBuffer in refs.PassBuffers)
			RemoveBufferRef(passBuffer);
		RemoveBufferRef(refs.BindlessCountBuffer);
		RemoveBufferRef(refs.BindlessTextureBuffer);
		RemoveBufferRef(refs.BindlessRwTextureBuffer);
		RemoveBufferRef(refs.BindlessSamplerBuffer);
	}

	private void AddBufferRef(MTLBuffer buffer, MetalBuffer? owner = null)
	{
		if (buffer.NativePtr == IntPtr.Zero)
		{
			return;
		}

		var key = buffer.NativePtr;
		if (_bufferRefsByPointer.TryGetValue(key, out var entry))
		{
			if (entry.Owner is not null && owner is not null && ReferenceEquals(entry.Owner, owner) == false)
			{
				throw new InvalidOperationException("A Metal buffer pointer is associated with multiple managed owners.");
			}

			if (entry.Owner is null && owner is not null)
			{
				owner.RetainForIndirectUse();
			}

			_bufferRefsByPointer[key] =
				new BufferRefEntry(entry.Buffer, entry.Owner ?? owner, entry.RefCount + 1, entry.Index);
			return;
		}

		owner?.RetainForIndirectUse();
		var index = _referencedBuffers.Count;
		_referencedBuffers.Add(buffer);
		_bufferRefsByPointer[key] = new BufferRefEntry(buffer, owner, 1, index);
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
			_bufferRefsByPointer[key] =
				new BufferRefEntry(entry.Buffer, entry.Owner, entry.RefCount - 1, entry.Index);
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
				_bufferRefsByPointer[movedKey] =
					new BufferRefEntry(movedEntry.Buffer, movedEntry.Owner, movedEntry.RefCount, removeIndex);
			}
		}

		_referencedBuffers.RemoveAt(lastIndex);
		_bufferRefsByPointer.Remove(key);
		entry.Owner?.ReleaseFromIndirectUse();
	}
}
