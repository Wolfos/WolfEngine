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
	[StructLayout(LayoutKind.Sequential)]
	internal struct CommandRecord
	{
		public Silk.NET.Direct3D12.VertexBufferView VertexBufferView;
		public Silk.NET.Direct3D12.IndexBufferView IndexBufferView;
		public ulong SrvT10Address;
		public ulong SrvT11Address;
		public ulong SrvT12Address;
		public ulong SrvT13Address;
		public ulong SrvT14Address;
		public ulong SrvT15Address;
		public ulong SrvT16Address;
		public ulong CbvB16Address;
		public DrawIndexedArguments DrawArguments;
	}

	private readonly record struct TrackedReference(
		D3D12Buffer Buffer,
		ulong BakedAddress,
		ResourceStates RequiredStates);

	private readonly ComPtr<ID3D12Resource> _argumentBuffer;
	private readonly CommandRecord* _mappedRecords;
	private readonly ulong _recordStride;

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
	}

	public string? Name { get; }

	public IndirectCommandBufferDescriptor Descriptor { get; }

	internal ComPtr<ID3D12CommandSignature> CommandSignature { get; }

	internal ID3D12Resource* ArgumentBuffer => _argumentBuffer.Handle;

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

		var record = new CommandRecord
		{
			VertexBufferView = new Silk.NET.Direct3D12.VertexBufferView
			{
				// Keep the input-assembler views rooted at the packed streams.  The
				// indexed draw and GpuMeshData both use the same global start/base
				// offsets, which avoids mixing two addressing conventions.
				BufferLocation = vertexResource->GetGPUVirtualAddress(),
				StrideInBytes = mesh.StrideInBytes,
				SizeInBytes = (uint)Math.Min(vertexBuffer.SizeInBytes, uint.MaxValue)
			},
			IndexBufferView = new Silk.NET.Direct3D12.IndexBufferView
			{
				BufferLocation = indexResource->GetGPUVirtualAddress(),
				SizeInBytes = (uint)Math.Min(indexBuffer.SizeInBytes, uint.MaxValue),
				Format = Format.FormatR32Uint
			},
			SrvT10Address = instanceBuffer.Resource.Handle->GetGPUVirtualAddress(),
			SrvT11Address = materialBuffer.Resource.Handle->GetGPUVirtualAddress(),
			SrvT12Address = drawArgsBuffer.Resource.Handle->GetGPUVirtualAddress()
			                 + drawArgsBaseOffsetBytes
			                 + (drawArgsCommandIndex * (ulong)Marshal.SizeOf<GpuDrawArgs>()),
			SrvT13Address = materialGenerationBuffer.Resource.Handle->GetGPUVirtualAddress(),
			DrawArguments = new DrawIndexedArguments
			{
				IndexCountPerInstance = mesh.IndexCount,
				InstanceCount = 1,
				StartIndexLocation = checked((uint)(mesh.PackedIndexOffsetBytes / sizeof(uint))),
				BaseVertexLocation = mesh.PackedBaseVertex,
				StartInstanceLocation = 0
			}
		};
		// Re-encoding replaces this command's references outright, so a buffer the previous encoding
		// pointed at stops being tracked the moment it stops being referenced.
		ClearCommandReferences(commandIndex);
		TrackBuffer(commandIndex, vertexBuffer, ResourceStates.VertexAndConstantBuffer);
		TrackBuffer(commandIndex, indexBuffer, ResourceStates.IndexBuffer);
		TrackBuffer(commandIndex, instanceBuffer, ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource);
		TrackBuffer(commandIndex, materialBuffer, ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource);
		TrackBuffer(commandIndex, drawArgsBuffer, ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource);
		TrackBuffer(commandIndex, materialGenerationBuffer, ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource);
		foreach (var binding in passBindings.Bindings)
		{
			if (binding.Resource is not D3D12Buffer passBuffer || passBuffer.Resource.Handle is null)
			{
				ResetCommand(commandIndex);
				return;
			}

			SetPassBindingAddress(
				ref record,
				binding.Kind,
				binding.RegisterIndex,
				passBuffer.Resource.Handle->GetGPUVirtualAddress());
			TrackBuffer(
				commandIndex,
				passBuffer,
				binding.Kind == GraphicsPassBindingKind.ConstantBuffer
					? ResourceStates.VertexAndConstantBuffer
					: ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource);
		}

		_mappedRecords[commandIndex] = record;
	}

	public void Dispose()
	{
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

	private static void SetPassBindingAddress(
		ref CommandRecord record,
		GraphicsPassBindingKind kind,
		uint registerIndex,
		ulong gpuAddress)
	{
		if (kind == GraphicsPassBindingKind.ConstantBuffer)
		{
			switch (registerIndex)
			{
				case 16: record.CbvB16Address = gpuAddress; return;
				default: return; // Other pass CBVs are inherited from command-list state.
			}
		}
		else
		{
			switch (registerIndex)
			{
				case 10: record.SrvT10Address = gpuAddress; return;
				case 11: record.SrvT11Address = gpuAddress; return;
				case 12: record.SrvT12Address = gpuAddress; return;
				case 13: record.SrvT13Address = gpuAddress; return;
				case 14: record.SrvT14Address = gpuAddress; return;
				case 15: record.SrvT15Address = gpuAddress; return;
				case 16: record.SrvT16Address = gpuAddress; return;
			}
		}

		throw new InvalidOperationException($"Unsupported D3D12 indirect pass binding {(kind == GraphicsPassBindingKind.ConstantBuffer ? 'b' : 't')}{registerIndex}.");
	}
}
