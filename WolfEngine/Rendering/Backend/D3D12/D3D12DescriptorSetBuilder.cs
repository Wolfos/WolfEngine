#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.D3D12;

internal sealed unsafe class D3D12DescriptorSetBuilder : IGfxDescriptorSetBuilder
{
	private readonly ComPtr<ID3D12Device> _device;
	private readonly List<Entry> _entries = new();

	public D3D12DescriptorSetBuilder(ComPtr<ID3D12Device> device)
	{
		_device = device;
	}

	public void AddShaderResource(uint slot, IGfxResource resource)
	{
		_entries.Add(new Entry(slot, DescriptorKind.ShaderResource, resource));
	}

	public void AddUnorderedAccess(uint slot, IGfxResource resource)
	{
		_entries.Add(new Entry(slot, DescriptorKind.UnorderedAccess, resource));
	}

	public void AddConstantBuffer(uint slot, IGfxBuffer buffer)
	{
		_entries.Add(new Entry(slot, DescriptorKind.ConstantBuffer, buffer));
	}

	public IGfxDescriptorSet Build()
	{
		if (_entries.Count == 0)
		{
			throw new InvalidOperationException("Cannot build a descriptor set with no entries.");
		}

		var descriptorSize = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.CbvSrvUav);
		var descriptorCount = 1 + (int)_entries.Max(e => e.Slot);

		var heapDesc = new DescriptorHeapDesc
		{
			Type = DescriptorHeapType.CbvSrvUav,
			NumDescriptors = (uint)descriptorCount,
			Flags = DescriptorHeapFlags.ShaderVisible
		};

		SilkMarshal.ThrowHResult(_device.CreateDescriptorHeap(in heapDesc, out ComPtr<ID3D12DescriptorHeap> heap));

		var cpuStart = heap.Handle->GetCPUDescriptorHandleForHeapStart();

		foreach (var entry in _entries)
		{
			var cpuHandle = cpuStart;
			cpuHandle.Ptr += descriptorSize * entry.Slot;
			switch (entry.Kind)
			{
				case DescriptorKind.ShaderResource:
					CreateSrv(entry.Resource, cpuHandle);
					break;
				case DescriptorKind.UnorderedAccess:
					CreateUav(entry.Resource, cpuHandle);
					break;
				case DescriptorKind.ConstantBuffer:
					CreateCbv(entry.Resource as IGfxBuffer
					          ?? throw new InvalidOperationException("Descriptor slot expected a buffer for CBV."),
						cpuHandle);
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(entry.Kind), entry.Kind, "Unsupported descriptor kind.");
			}
		}

		var gpuStart = heap.Handle->GetGPUDescriptorHandleForHeapStart();
		var handles = new Dictionary<uint, GpuDescriptorHandle>(_entries.Count);
		foreach (var entry in _entries)
		{
			var handle = gpuStart;
			handle.Ptr += descriptorSize * entry.Slot;
			handles[entry.Slot] = handle;
		}

		return new D3D12DescriptorSet(heap, gpuStart, handles);
	}

	private void CreateSrv(IGfxResource resource, CpuDescriptorHandle cpuHandle)
	{
		if (resource is not ID3D12BackendTexture texture)
		{
			throw new InvalidOperationException("SRV descriptors currently support textures only.");
		}

			const uint DefaultShader4ComponentMapping = 5768; // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING

			var descriptor = new ShaderResourceViewDesc
			{
				Shader4ComponentMapping = DefaultShader4ComponentMapping,
				ViewDimension = SrvDimension.Texture2D,
				Format = texture.Descriptor.Format switch
				{
					TextureFormat.D32Float => Format.FormatR32Float,
					TextureFormat.Bgra8Unorm => texture.Descriptor.IsSrgb ? Format.FormatB8G8R8A8UnormSrgb : Format.FormatB8G8R8A8Unorm,
					TextureFormat.Rgba8Unorm => texture.Descriptor.IsSrgb ? Format.FormatR8G8B8A8UnormSrgb : Format.FormatR8G8B8A8Unorm,
					TextureFormat.Rgba8Uint => Format.FormatR8G8B8A8Uint,
					TextureFormat.R16Unorm => Format.FormatR16Unorm,
					TextureFormat.Rg16Float => Format.FormatR16G16Float,
					TextureFormat.Rgba16Float => Format.FormatR16G16B16A16Float,
					TextureFormat.R32Float => Format.FormatR32Float,
				TextureFormat.R32Uint => Format.FormatR32Uint,
					TextureFormat.Bc1Unorm => texture.Descriptor.IsSrgb ? Format.FormatBC1UnormSrgb : Format.FormatBC1Unorm,
					TextureFormat.Bc3Unorm => texture.Descriptor.IsSrgb ? Format.FormatBC3UnormSrgb : Format.FormatBC3Unorm,
					TextureFormat.Bc4Unorm => Format.FormatBC4Unorm,
					TextureFormat.Bc5Unorm => Format.FormatBC5Unorm,
					TextureFormat.Bc7Unorm => texture.Descriptor.IsSrgb ? Format.FormatBC7UnormSrgb : Format.FormatBC7Unorm,
					_ => Format.FormatUnknown
				}
			};
		descriptor.Anonymous.Texture2D.MostDetailedMip = 0;
		descriptor.Anonymous.Texture2D.MipLevels = (uint)texture.Descriptor.MipLevels;
		descriptor.Anonymous.Texture2D.ResourceMinLODClamp = 0;

		_device.Handle->CreateShaderResourceView(texture.Resource, &descriptor, cpuHandle);
	}

	private void CreateUav(IGfxResource resource, CpuDescriptorHandle cpuHandle)
	{
		if (resource is not ID3D12BackendTexture texture)
		{
			throw new InvalidOperationException("UAV descriptors currently support textures only.");
		}

		_device.Handle->CreateUnorderedAccessView(texture.Resource, null, null, cpuHandle);
	}

	private void CreateCbv(IGfxBuffer buffer, CpuDescriptorHandle cpuHandle)
	{
		if (buffer is not D3D12Buffer d3d12Buffer)
		{
			throw new InvalidOperationException("Constant buffer was not created by the Direct3D12 backend.");
		}

		var cbvDesc = new ConstantBufferViewDesc
		{
			BufferLocation = d3d12Buffer.Resource.Handle->GetGPUVirtualAddress(),
			SizeInBytes = d3d12Buffer.GetConstantBufferViewSizeInBytes()
		};

		_device.Handle->CreateConstantBufferView(in cbvDesc, cpuHandle);
	}

	private readonly record struct Entry(uint Slot, DescriptorKind Kind, IGfxResource Resource);

	private enum DescriptorKind
	{
		ShaderResource,
		UnorderedAccess,
		ConstantBuffer
	}
}

internal sealed class D3D12DescriptorSet : IGfxDescriptorSet
{
	private readonly IReadOnlyDictionary<uint, GpuDescriptorHandle> _handles;

	public D3D12DescriptorSet(ComPtr<ID3D12DescriptorHeap> heap, GpuDescriptorHandle gpuHandle,
		IReadOnlyDictionary<uint, GpuDescriptorHandle> handles)
	{
		DescriptorHeap = heap;
		GpuHandle = gpuHandle;
		_handles = handles;
	}

	public ComPtr<ID3D12DescriptorHeap> DescriptorHeap { get; }

	public GpuDescriptorHandle GpuHandle { get; }

	public GpuDescriptorHandle GetGpuHandle(uint slot)
	{
		if (_handles.TryGetValue(slot, out var handle) == false)
		{
			throw new ArgumentOutOfRangeException(nameof(slot), slot, "Slot not found in descriptor set.");
		}

		return handle;
	}

	public void Dispose()
	{
		DescriptorHeap.Dispose();
	}
}
