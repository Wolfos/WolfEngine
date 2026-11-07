using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering.Backend.Metal;

[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
internal sealed unsafe class MetalIndirectCommandBuffer : IGfxIndirectCommandBuffer, IDisposable
{
	/// <summary>
	/// Per command, what compaction needs to issue the command's draw again with the visible instance count.
	/// Mirrors <c>IndirectDrawGeometry</c> in gpu_draw_compact_icb.metal.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	internal struct CompactionDrawGeometry
	{
		public uint IndexCount;
		public uint IndexOffsetElements;
	}

	private readonly Dictionary<uint, CommandBufferReferences> _commandReferences = new();
	private readonly Dictionary<nint, BufferRefEntry> _bufferRefsByPointer = new();
	private readonly List<MTLBuffer> _referencedBuffers = new();
	// The index buffer every encoded command draws from, and how many commands do. Compaction binds a single
	// index buffer per page, so commands that disagree on it cannot be compacted.
	private MTLBuffer _indexBuffer;
	private int _indexBufferCommandCount;

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
			MTLBuffer visibleInstanceBuffer,
			MTLBuffer[] passBuffers,
			MetalBuffer vertexBufferOwner,
			MetalBuffer indexBufferOwner,
			MetalBuffer instanceBufferOwner,
			MetalBuffer materialBufferOwner,
			MetalBuffer materialGenerationBufferOwner,
			MetalBuffer drawArgsBufferOwner,
			MetalBuffer visibleInstanceBufferOwner,
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
			VisibleInstanceBuffer = visibleInstanceBuffer;
			PassBuffers = passBuffers;
			VertexBufferOwner = vertexBufferOwner;
			IndexBufferOwner = indexBufferOwner;
			InstanceBufferOwner = instanceBufferOwner;
			MaterialBufferOwner = materialBufferOwner;
			MaterialGenerationBufferOwner = materialGenerationBufferOwner;
			DrawArgsBufferOwner = drawArgsBufferOwner;
			VisibleInstanceBufferOwner = visibleInstanceBufferOwner;
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
		public MTLBuffer VisibleInstanceBuffer { get; }
		public MTLBuffer[] PassBuffers { get; }
		public MetalBuffer VertexBufferOwner { get; }
		public MetalBuffer IndexBufferOwner { get; }
		public MetalBuffer InstanceBufferOwner { get; }
		public MetalBuffer MaterialBufferOwner { get; }
		public MetalBuffer MaterialGenerationBufferOwner { get; }
		public MetalBuffer DrawArgsBufferOwner { get; }
		public MetalBuffer VisibleInstanceBufferOwner { get; }
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
		MTLBuffer compactionArguments = default,
		MTLBuffer compactionGeometry = default)
	{
		Name = name;
		Descriptor = descriptor;
		Buffer = buffer;
		CompactedBuffer = compactedBuffer;
		CompactionArguments = compactionArguments;
		CompactionGeometry = compactionGeometry;
		GpuMemoryCensus.Track(this, "Indirect command buffer", buffer.NativePtr, name);
		GpuMemoryCensus.Track(this, "Indirect command buffer (compacted)", compactedBuffer.NativePtr, name);
		GpuMemoryCensus.Track(this, "Indirect compaction arguments", compactionArguments.NativePtr, name);
		GpuMemoryCensus.Track(this, "Indirect compaction geometry", compactionGeometry.NativePtr, name);
	}

	/// <summary>Creates the zeroed per-command geometry table that compaction issues draws from.</summary>
	internal static MTLBuffer CreateCompactionGeometryBuffer(MTLDevice device, uint commandCapacity)
	{
		var sizeInBytes = (ulong)commandCapacity * (ulong)sizeof(CompactionDrawGeometry);
		var buffer = device.NewBuffer(sizeInBytes, MTLResourceOptions.ResourceStorageModeShared);
		if (buffer.NativePtr == IntPtr.Zero)
		{
			return default;
		}

		new Span<byte>(buffer.Contents.ToPointer(), checked((int)sizeInBytes)).Clear();
		return buffer;
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

	/// <summary>
	/// <see cref="CompactionDrawGeometry"/> per command. Compaction cannot patch the instance count of a copied
	/// command, so it issues the draw again from this, with the instance count culling left visible.
	/// </summary>
	internal MTLBuffer CompactionGeometry { get; }

	/// <summary>The index buffer every encoded command draws from, or null-valued while none is encoded.</summary>
	internal MTLBuffer CompactionIndexBuffer => _indexBufferCommandCount > 0 ? _indexBuffer : default;

	public IndirectCompactionKind CompactionKind =>
		CompactedBuffer.NativePtr != IntPtr.Zero &&
		CompactionArguments.NativePtr != IntPtr.Zero &&
		CompactionGeometry.NativePtr != IntPtr.Zero
			? IndirectCompactionKind.NativeCommands
			: IndirectCompactionKind.None;

	public void ResetCommand(uint commandIndex)
	{
		ValidateCommandIndex(commandIndex);
		Buffer.Reset(new NSRange { location = commandIndex, length = 1 });
		WriteCompactionGeometry(commandIndex, default);
		if (_commandReferences.TryGetValue(commandIndex, out var refs))
		{
			RemoveBufferRefs(in refs);
			ReleaseIndexBuffer();
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
		uint instanceCount,
		ulong drawArgsOffsetBytes,
		MetalBuffer instanceBuffer,
		MetalBuffer materialBuffer,
		MetalBuffer materialGenerationBuffer,
		MetalBuffer drawArgsBuffer,
		MetalBuffer visibleInstanceBuffer,
		GraphicsPassBindingSet passBindings,
		in SharedDrawPerDrawBindings perDrawBindings,
		MTLBuffer bindlessCountBuffer,
		MTLBuffer bindlessTextureBuffer,
		MTLBuffer bindlessRwTextureBuffer,
		MTLBuffer bindlessSamplerBuffer)
	{
		ValidateCommandIndex(commandIndex);
		if (indexFormat != IndexFormat.UInt32 || indexBufferOffsetBytes % sizeof(uint) != 0)
		{
			throw new InvalidOperationException(
				"Compaction re-issues draws from 32-bit indices, so indirect draws must use aligned 32-bit indices.");
		}

		if (_commandReferences.TryGetValue(commandIndex, out var previousRefs))
		{
			RemoveBufferRefs(in previousRefs);
			ReleaseIndexBuffer();
			_commandReferences.Remove(commandIndex);
		}

		AcquireIndexBuffer(indexBuffer.Buffer);

		// Not disposed: the command is owned by the indirect command buffer and returned autoreleased.
		var command = Buffer.IndirectRenderCommand(commandIndex);
		command.Reset();
		command.SetVertexBuffer(vertexBuffer.Buffer, vertexBufferOffsetBytes, 0);
		command.SetVertexBuffer(instanceBuffer.Buffer, 0, perDrawBindings.InstanceRegisterIndex);
		command.SetVertexBuffer(materialBuffer.Buffer, 0, perDrawBindings.MaterialRegisterIndex);
		command.SetVertexBuffer(materialGenerationBuffer.Buffer, 0, perDrawBindings.MaterialGenerationRegisterIndex);
		command.SetVertexBuffer(drawArgsBuffer.Buffer, drawArgsOffsetBytes, perDrawBindings.DrawArgsRegisterIndex);
		command.SetVertexBuffer(visibleInstanceBuffer.Buffer, 0, perDrawBindings.VisibleInstanceRegisterIndex);
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
			instanceCount,
			baseVertex,
			0);
		WriteCompactionGeometry(
			commandIndex,
			new CompactionDrawGeometry
			{
				IndexCount = indexCount,
				IndexOffsetElements = checked((uint)(indexBufferOffsetBytes / sizeof(uint)))
			});

		_commandReferences[commandIndex] = new CommandBufferReferences(
			vertexBuffer.Buffer,
			indexBuffer.Buffer,
			instanceBuffer.Buffer,
			materialBuffer.Buffer,
			materialGenerationBuffer.Buffer,
			drawArgsBuffer.Buffer,
			visibleInstanceBuffer.Buffer,
			passBuffers,
			vertexBuffer,
			indexBuffer,
			instanceBuffer,
			materialBuffer,
			materialGenerationBuffer,
			drawArgsBuffer,
			visibleInstanceBuffer,
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
			AddBuffer(refs.VisibleInstanceBuffer, destination, seenPointers);
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
		GpuMemoryCensus.Untrack(this);
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

		if (CompactionGeometry.NativePtr != IntPtr.Zero)
		{
			CompactionGeometry.Dispose();
		}
	}

	private void WriteCompactionGeometry(uint commandIndex, CompactionDrawGeometry geometry)
	{
		if (CompactionGeometry.NativePtr == IntPtr.Zero)
		{
			return;
		}

		((CompactionDrawGeometry*)CompactionGeometry.Contents.ToPointer())[commandIndex] = geometry;
	}

	private void AcquireIndexBuffer(MTLBuffer indexBuffer)
	{
		if (_indexBufferCommandCount > 0 && _indexBuffer.NativePtr != indexBuffer.NativePtr)
		{
			throw new InvalidOperationException(
				$"Indirect command buffer '{Name ?? "<unnamed>"}' would draw from two index buffers, but compaction " +
				"binds one per page. Every shared draw must come from the packed index buffer.");
		}

		_indexBuffer = indexBuffer;
		_indexBufferCommandCount++;
	}

	private void ReleaseIndexBuffer()
	{
		_indexBufferCommandCount--;
		if (_indexBufferCommandCount == 0)
		{
			_indexBuffer = default;
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
		AddBufferRef(refs.VisibleInstanceBuffer, refs.VisibleInstanceBufferOwner);
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
		RemoveBufferRef(refs.VisibleInstanceBuffer);
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
