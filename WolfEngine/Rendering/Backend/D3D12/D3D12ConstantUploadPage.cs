using System;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace WolfEngine.Rendering.Backend.D3D12;

internal sealed unsafe class D3D12ConstantUploadPage : IDisposable
{
	public D3D12ConstantUploadPage(ComPtr<ID3D12Resource> resource, byte* mappedData, ulong sizeInBytes)
	{
		Resource = resource;
		MappedData = mappedData;
		SizeInBytes = sizeInBytes;
		GpuAddress = resource.Handle is null ? 0UL : resource.Handle->GetGPUVirtualAddress();
	}

	public ComPtr<ID3D12Resource> Resource { get; private set; }

	public byte* MappedData { get; private set; }

	public ulong GpuAddress { get; }

	public ulong SizeInBytes { get; }

	public void Dispose()
	{
		if (Resource.Handle is null)
		{
			return;
		}

		Resource.Unmap(0, (Silk.NET.Direct3D12.Range*)null);
		Resource.Dispose();
		Resource = default;
		MappedData = null;
	}
}
