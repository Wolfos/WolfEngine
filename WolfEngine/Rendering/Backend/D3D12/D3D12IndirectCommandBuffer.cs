#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using WolfEngine.Rendering;
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

	private readonly record struct TrackedReference(
		D3D12Buffer Buffer,
		ulong BakedAddress,
		ResourceStates RequiredStates);

	private readonly ComPtr<ID3D12Resource> _argumentBuffer;
	private readonly CommandRecord* _mappedRecords;
	private readonly ulong _recordStride;

	// Compaction reads the CPU-encoded records through _templateView and writes the visible subset into
	// _compactedBuffer, which is what ExecuteIndirect then consumes. Both alias resources this buffer
	// owns, so they are disposed here rather than by whoever binds them.
	private readonly D3D12Buffer? _templateView;
	private readonly D3D12Buffer? _compactedBuffer;

	// Tracked per command, not per page. A page-wide set only ever grows: re-encoding a command would
	// leave the buffers it used to reference in the set, and they would then be reported as dangling
	// once released even though no live record points at them.
	private readonly List<TrackedReference>?[] _commandReferences;
	private readonly Dictionary<D3D12Buffer, ResourceStates> _referencedBufferStates = new();
	private readonly Dictionary<D3D12Buffer, ulong> _referencedBufferAddresses = new();
	private bool _aggregatesDirty = true;

	private string? _lastStaleReport;
	private int _staleReportCount;

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
		_commandReferences = new List<TrackedReference>?[descriptor.MaxCommandCount];

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

	internal IEnumerable<KeyValuePair<D3D12Buffer, ResourceStates>> ReferencedBufferStates
	{
		get
		{
			RebuildAggregates();
			return _referencedBufferStates;
		}
	}

	public void ResetCommand(uint commandIndex)
	{
		ValidateCommandIndex(commandIndex);
		_mappedRecords[commandIndex] = default;
		ClearCommandReferences(commandIndex);
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
		in SharedDrawPerDrawBindings perDrawBindings)
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
		if (mesh.PackedVertexOffsetBytes >= vertexBuffer.SizeInBytes ||
		    mesh.PackedIndexOffsetBytes > indexBuffer.SizeInBytes ||
		    meshIndexBytes > indexBuffer.SizeInBytes - mesh.PackedIndexOffsetBytes)
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
				StartIndexLocation = checked((uint)(mesh.PackedIndexOffsetBytes / sizeof(uint))),
				BaseVertexLocation = mesh.PackedBaseVertex,
				StartInstanceLocation = 0
			}
		};

		// Records hold no GPU addresses now, so nothing here can dangle when a buffer is replaced by
		// capacity growth. The reference tracking is kept only to satisfy the diagnostic reporting.
		ClearCommandReferences(commandIndex);
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

	private void TrackBuffer(uint commandIndex, D3D12Buffer buffer, ResourceStates requiredState)
	{
		var resource = buffer.Resource.Handle;
		if (resource is null)
		{
			return;
		}

		var references = _commandReferences[commandIndex] ??= new List<TrackedReference>();
		references.Add(new TrackedReference(buffer, resource->GetGPUVirtualAddress(), requiredState));
		_aggregatesDirty = true;
	}

	private void ClearCommandReferences(uint commandIndex)
	{
		var references = _commandReferences[commandIndex];
		if (references is null || references.Count == 0)
		{
			return;
		}

		references.Clear();
		_aggregatesDirty = true;
	}

	/// <summary>
	/// Folds the per-command references back into the per-page views used for barriers and staleness
	/// reporting. Only the buffers currently referenced by a live record survive.
	/// </summary>
	private void RebuildAggregates()
	{
		if (_aggregatesDirty == false)
		{
			return;
		}

		_referencedBufferStates.Clear();
		_referencedBufferAddresses.Clear();
		for (var commandIndex = 0; commandIndex < _commandReferences.Length; commandIndex++)
		{
			var references = _commandReferences[commandIndex];
			if (references is null)
			{
				continue;
			}

			for (var i = 0; i < references.Count; i++)
			{
				var reference = references[i];
				_referencedBufferAddresses[reference.Buffer] = reference.BakedAddress;
				_referencedBufferStates[reference.Buffer] =
					_referencedBufferStates.TryGetValue(reference.Buffer, out var existingState)
						? existingState | reference.RequiredStates
						: reference.RequiredStates;
			}
		}

		_aggregatesDirty = false;
	}

	internal bool TryDescribeStaleReferences(out string description)
	{
		RebuildAggregates();
		List<string>? stale = null;
		foreach (var (buffer, bakedAddress) in _referencedBufferAddresses)
		{
			var resource = buffer.Resource.Handle;
			if (resource is null)
			{
				(stale ??= new List<string>()).Add(
					$"'{buffer.Name ?? "<unnamed>"}' disposed (record holds 0x{bakedAddress:X16})");
				continue;
			}

			var currentAddress = resource->GetGPUVirtualAddress();
			if (currentAddress != bakedAddress)
			{
				(stale ??= new List<string>()).Add(
					$"'{buffer.Name ?? "<unnamed>"}' moved 0x{bakedAddress:X16} -> 0x{currentAddress:X16}");
			}
		}

		if (stale is null)
		{
			_lastStaleReport = null;
			description = string.Empty;
			return false;
		}

		_staleReportCount++;
		description = $"indirect command buffer '{Name ?? "<unnamed>"}': {string.Join(", ", stale)}";
		if (string.Equals(_lastStaleReport, description, StringComparison.Ordinal))
		{
			// Report the same staleness on a curve rather than once, ever. Reporting once hides whether a
			// dangling reference is a one-off or is being executed on every frame up to the hang, and the
			// single line is easy to miss; reporting every frame drowns the log it is meant to inform.
			if (BitOperations.IsPow2(_staleReportCount) == false)
			{
				description = string.Empty;
				return false;
			}

			description = $"{description} [occurrence {_staleReportCount}]";
			return true;
		}

		_lastStaleReport = description;
		_staleReportCount = 1;
		return true;
	}

}
