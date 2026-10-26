using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering.Backend.D3D12;

internal sealed unsafe class D3D12IndirectCommandBuffer : IGfxIndirectCommandBuffer, IDisposable
{
	/// <summary>
	/// Must match the indirect command signature built in <see cref="D3D12Device"/>: one dword of root
	/// constants carrying the draw index, then the draw arguments. The buffers a draw reads are bound
	/// once per pass instead of per command, which is what keeps this record down to 24 bytes.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	internal struct CommandRecord
	{
		public uint DrawIndex;
		public DrawIndexedArguments DrawArguments;
	}

	private readonly ComPtr<ID3D12Resource> _argumentBuffer;
	private readonly CommandRecord* _mappedRecords;
	private readonly ulong _recordStride;

	// Compaction reads the CPU-encoded records through _templateView and writes the visible subset into
	// _compactedBuffer, which is what ExecuteIndirect then consumes. Both alias resources this buffer
	// owns, so they are disposed here rather than by whoever binds them.
	private readonly D3D12Buffer? _templateView;
	private readonly D3D12Buffer? _compactedBuffer;

	public D3D12IndirectCommandBuffer(
		string? name,
		in IndirectCommandBufferDescriptor descriptor,
		ComPtr<ID3D12Device> device,
		ComPtr<ID3D12CommandSignature> commandSignature)
	{
		Name = name;
		Descriptor = descriptor;
		CommandSignature = commandSignature;
		_recordStride = (ulong)sizeof(CommandRecord);

		var totalSize = _recordStride * descriptor.MaxCommandCount;
		var desc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = totalSize,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = Format.FormatUnknown,
			SampleDesc = new SampleDesc(1, 0),
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.None
		};
		var props = new HeapProperties(HeapType.Upload);
		SilkMarshal.ThrowHResult(device.CreateCommittedResource(
			&props,
			HeapFlags.None,
			in desc,
			ResourceStates.GenericRead,
			null,
			out _argumentBuffer));

		void* mapped = null;
		SilkMarshal.ThrowHResult(_argumentBuffer.Map(0, (Silk.NET.Direct3D12.Range*)null, &mapped));
		_mappedRecords = (CommandRecord*)mapped;

		for (var i = 0u; i < descriptor.MaxCommandCount; i++)
		{
			ResetCommand(i);
		}

		// Graphics command buffers are the ones culling feeds; compute ones have no visibility to
		// compact against, so they keep the plain full-range execution path.
		if (descriptor.PassKind == PassKind.Graphics)
		{
			var compactedDesc = desc;
			compactedDesc.Flags = ResourceFlags.AllowUnorderedAccess;
			var compactedProps = new HeapProperties(HeapType.Default);
			// D3D12 creates default-heap buffers in COMMON regardless of the state requested here, so
			// the wrapper below has to start tracking from COMMON or its first transition is skipped.
			SilkMarshal.ThrowHResult(device.CreateCommittedResource(
				&compactedProps,
				HeapFlags.None,
				in compactedDesc,
				ResourceStates.Common,
				null,
				out ComPtr<ID3D12Resource> compactedResource));

			if (name is not null)
			{
				_ = compactedResource.SetName($"{name} compacted");
			}

			var bufferDescriptor = new BufferDescriptor(
				totalSize,
				BufferUsage.Indirect,
				BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource,
				name: name);

			_compactedBuffer = new D3D12Buffer(
				name is null ? null : $"{name} compacted",
				bufferDescriptor,
				compactedResource,
				totalSize,
				initialState: ResourceStates.Common);

			// The template lives on the upload heap and must stay in GENERIC_READ, so it is flagged
			// CPU-writable to keep the binding path from transitioning it.
			_templateView = new D3D12Buffer(
				name is null ? null : $"{name} template",
				bufferDescriptor,
				_argumentBuffer,
				totalSize,
				cpuWritableDirect: true,
				initialState: ResourceStates.GenericRead);
		}
	}

	public string? Name { get; }

	public IndirectCommandBufferDescriptor Descriptor { get; }

	internal ComPtr<ID3D12CommandSignature> CommandSignature { get; }

	internal ID3D12Resource* ArgumentBuffer => _argumentBuffer.Handle;

	public IndirectCompactionKind CompactionKind =>
		_compactedBuffer is null ? IndirectCompactionKind.None : IndirectCompactionKind.CommandRecords;

	public IGfxBuffer? TemplateRecordBuffer => _templateView;

	public IGfxBuffer? CompactedRecordBuffer => _compactedBuffer;

	public uint RecordStrideInBytes => (uint)_recordStride;

	public uint RecordIndexCountOffsetInBytes =>
		(uint)(Marshal.OffsetOf<CommandRecord>(nameof(CommandRecord.DrawArguments)) +
		       Marshal.OffsetOf<DrawIndexedArguments>(nameof(DrawIndexedArguments.IndexCountPerInstance)));

	internal D3D12Buffer? CompactedBuffer => _compactedBuffer;

	internal ulong ArgumentStride => _recordStride;

	internal ulong GetArgumentOffset(uint commandIndex) => _recordStride * commandIndex;

	public void ResetCommand(uint commandIndex)
	{
		ValidateCommandIndex(commandIndex);
		_mappedRecords[commandIndex] = default;
	}

	public void EncodeIndexedDrawCommand(
		uint commandIndex,
		Mesh mesh,
		D3D12Buffer instanceBuffer,
		D3D12Buffer materialBuffer,
		D3D12Buffer drawArgsBuffer,
		D3D12Buffer materialGenerationBuffer,
		ulong drawArgsBaseOffsetBytes,
		uint drawArgsCommandIndex,
		GraphicsPassBindingSet passBindings,
		in SharedDrawPerDrawBindings perDrawBindings,
		MeshIndexStream indexStream = MeshIndexStream.Main)
	{
		ValidateCommandIndex(commandIndex);

		if (mesh.VertexBuffer is not D3D12Buffer vertexBuffer || mesh.IndexBuffer is not D3D12Buffer indexBuffer)
		{
			ResetCommand(commandIndex);
			return;
		}

		var vertexResource = vertexBuffer.Resource.Handle;
		var indexResource = indexBuffer.Resource.Handle;
		if (vertexResource is null || indexResource is null)
		{
			ResetCommand(commandIndex);
			return;
		}
		var meshIndexBytes = checked((ulong)mesh.IndexCount * sizeof(uint));
		var indexOffsetBytes = mesh.GetPackedIndexOffsetBytes(indexStream);
		if (mesh.PackedVertexOffsetBytes >= vertexBuffer.SizeInBytes ||
		    indexOffsetBytes > indexBuffer.SizeInBytes ||
		    meshIndexBytes > indexBuffer.SizeInBytes - indexOffsetBytes)
		{
			ResetCommand(commandIndex);
			return;
		}

		if (instanceBuffer.Resource.Handle is null ||
		    materialBuffer.Resource.Handle is null ||
		    drawArgsBuffer.Resource.Handle is null ||
		    materialGenerationBuffer.Resource.Handle is null)
		{
			ResetCommand(commandIndex);
			return;
		}

		// The mesh's place in the packed streams travels in the draw arguments, so the record no longer
		// needs the buffer views themselves: every command in a pass draws from the same two buffers.
		_mappedRecords[commandIndex] = new CommandRecord
		{
			DrawIndex = drawArgsCommandIndex,
			DrawArguments = new DrawIndexedArguments
			{
				IndexCountPerInstance = mesh.IndexCount,
				InstanceCount = 1,
				StartIndexLocation = checked((uint)(indexOffsetBytes / sizeof(uint))),
				BaseVertexLocation = mesh.PackedBaseVertex,
				StartInstanceLocation = 0
			}
		};

		// Records hold no GPU addresses, so nothing here can dangle when a buffer is replaced by capacity growth.
	}

	public void Dispose()
	{
		_compactedBuffer?.Dispose();

		// _templateView aliases _argumentBuffer without holding its own reference, so releasing it here
		// would double-release the resource that is disposed just below.
		if (_argumentBuffer.Handle is not null)
		{
			_argumentBuffer.Unmap(0, (Silk.NET.Direct3D12.Range*)null);
			_argumentBuffer.Dispose();
		}
	}

	private void ValidateCommandIndex(uint commandIndex)
	{
		if (commandIndex >= Descriptor.MaxCommandCount)
		{
			throw new ArgumentOutOfRangeException(nameof(commandIndex), commandIndex, "Command index is out of range.");
		}
	}
}
