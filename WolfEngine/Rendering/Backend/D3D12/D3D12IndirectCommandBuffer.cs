#nullable enable

using System;
using System.Collections.Generic;
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

	private readonly ComPtr<ID3D12Resource> _argumentBuffer;
	private readonly CommandRecord* _mappedRecords;
	private readonly ulong _recordStride;
	private readonly Dictionary<D3D12Buffer, ResourceStates> _referencedBufferStates = new();

	private readonly Dictionary<D3D12Buffer, ulong> _referencedBufferAddresses = new();

	private string? _lastStaleReport;

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
	}

	public string? Name { get; }

	public IndirectCommandBufferDescriptor Descriptor { get; }

	internal ComPtr<ID3D12CommandSignature> CommandSignature { get; }

	internal ID3D12Resource* ArgumentBuffer => _argumentBuffer.Handle;

	internal ulong ArgumentStride => _recordStride;

	internal ulong GetArgumentOffset(uint commandIndex) => _recordStride * commandIndex;

	internal IEnumerable<KeyValuePair<D3D12Buffer, ResourceStates>> ReferencedBufferStates => _referencedBufferStates;

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
		TrackBuffer(vertexBuffer, ResourceStates.VertexAndConstantBuffer);
		TrackBuffer(indexBuffer, ResourceStates.IndexBuffer);
		TrackBuffer(instanceBuffer, ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource);
		TrackBuffer(materialBuffer, ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource);
		TrackBuffer(drawArgsBuffer, ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource);
		TrackBuffer(materialGenerationBuffer, ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource);
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

	private void TrackBuffer(D3D12Buffer buffer, ResourceStates requiredState)
	{
		var resource = buffer.Resource.Handle;
		if (resource is not null)
		{
			_referencedBufferAddresses[buffer] = resource->GetGPUVirtualAddress();
		}

		if (_referencedBufferStates.TryGetValue(buffer, out var existingState))
		{
			_referencedBufferStates[buffer] = existingState | requiredState;
			return;
		}

		_referencedBufferStates.Add(buffer, requiredState);
	}

	internal bool TryDescribeStaleReferences(out string description)
	{
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

		description = $"indirect command buffer '{Name ?? "<unnamed>"}': {string.Join(", ", stale)}";
		if (string.Equals(_lastStaleReport, description, StringComparison.Ordinal))
		{
			description = string.Empty;
			return false;
		}

		_lastStaleReport = description;
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
