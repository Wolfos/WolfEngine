#nullable enable

using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.D3D12;

internal sealed unsafe class D3D12IndirectCommandBuffer : IGfxIndirectCommandBuffer, IDisposable
{
	[StructLayout(LayoutKind.Sequential)]
	internal struct CommandRecord
	{
		public Silk.NET.Direct3D12.VertexBufferView VertexBufferView;
		public Silk.NET.Direct3D12.IndexBufferView IndexBufferView;
		public ulong CbvB0Address;
		public ulong CbvB2Address;
		public ulong CbvB3Address;
		public ulong CbvB14Address;
		public ulong SrvT10Address;
		public ulong SrvT11Address;
		public ulong SrvT12Address;
		public ulong SrvT13Address;
		public DrawIndexedArguments DrawArguments;
	}

	private readonly ComPtr<ID3D12Resource> _argumentBuffer;
	private readonly CommandRecord* _mappedRecords;
	private readonly ulong _recordStride;

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
		D3D12Buffer cameraBuffer,
		D3D12Buffer shadowCameraBuffer,
		D3D12Buffer transparentEnvironmentBuffer,
		D3D12Buffer transparentLightingBuffer)
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

		if (instanceBuffer.Resource.Handle is null ||
		    materialBuffer.Resource.Handle is null ||
		    drawArgsBuffer.Resource.Handle is null ||
		    materialGenerationBuffer.Resource.Handle is null ||
		    cameraBuffer.Resource.Handle is null ||
		    shadowCameraBuffer.Resource.Handle is null ||
		    transparentEnvironmentBuffer.Resource.Handle is null ||
		    transparentLightingBuffer.Resource.Handle is null)
		{
			ResetCommand(commandIndex);
			return;
		}

		var record = new CommandRecord
		{
			VertexBufferView = new Silk.NET.Direct3D12.VertexBufferView
			{
				BufferLocation = vertexResource->GetGPUVirtualAddress() + mesh.PackedVertexOffsetBytes,
				StrideInBytes = mesh.StrideInBytes,
				SizeInBytes = (uint)Math.Min(vertexBuffer.SizeInBytes, uint.MaxValue)
			},
			IndexBufferView = new Silk.NET.Direct3D12.IndexBufferView
			{
				BufferLocation = indexResource->GetGPUVirtualAddress() + mesh.PackedIndexOffsetBytes,
				SizeInBytes = (uint)Math.Min(indexBuffer.SizeInBytes, uint.MaxValue),
				Format = Format.FormatR32Uint
			},
			CbvB0Address = transparentEnvironmentBuffer.Resource.Handle->GetGPUVirtualAddress(),
			CbvB2Address = cameraBuffer.Resource.Handle->GetGPUVirtualAddress(),
			CbvB3Address = transparentLightingBuffer.Resource.Handle->GetGPUVirtualAddress(),
			CbvB14Address = shadowCameraBuffer.Resource.Handle->GetGPUVirtualAddress(),
			SrvT10Address = instanceBuffer.Resource.Handle->GetGPUVirtualAddress(),
			SrvT11Address = materialBuffer.Resource.Handle->GetGPUVirtualAddress(),
			SrvT12Address = drawArgsBuffer.Resource.Handle->GetGPUVirtualAddress() + (commandIndex * (ulong)Marshal.SizeOf<GpuDrawArgs>()),
			SrvT13Address = materialGenerationBuffer.Resource.Handle->GetGPUVirtualAddress(),
			DrawArguments = new DrawIndexedArguments
			{
				IndexCountPerInstance = mesh.IndexCount,
				InstanceCount = 1,
				StartIndexLocation = 0,
				BaseVertexLocation = mesh.PackedBaseVertex,
				StartInstanceLocation = 0
			}
		};

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
}
