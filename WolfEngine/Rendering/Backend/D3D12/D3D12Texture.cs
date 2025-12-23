using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Backend.D3D12;

namespace WolfEngine.Backend.D3D12;

internal sealed unsafe class D3D12Texture : ID3D12BackendTexture, IDisposable
{
	private TextureDescriptor _descriptor;
	private ComPtr<ID3D12DescriptorHeap> _rtvHeap;
	private ComPtr<ID3D12DescriptorHeap> _dsvHeap;
	private CpuDescriptorHandle? _rtvHandle;
	private CpuDescriptorHandle? _dsvHandle;
	private DescriptorHandle _srvHandle = DescriptorHandle.Invalid;
	private DescriptorHandle _uavHandle = DescriptorHandle.Invalid;

	public D3D12Texture(string name, TextureDescriptor descriptor, ComPtr<ID3D12Resource> resource)
	{
		Initialize(name, descriptor, resource);
	}

	internal D3D12Texture()
	{
	}

	public string Name { get; private set; }

	public TextureDescriptor Descriptor => _descriptor;

	public ComPtr<ID3D12Resource> Resource { get; private set; }

	public CpuDescriptorHandle? RenderTargetView => _rtvHandle;

	public CpuDescriptorHandle? DepthStencilView => _dsvHandle;

	public DescriptorHandle ShaderResourceView => _srvHandle;

	public DescriptorHandle UnorderedAccessView => _uavHandle;

	ID3D12Resource* ID3D12BackendTexture.Resource => Resource.Handle;

	internal void Initialize(string name, in TextureDescriptor descriptor, ComPtr<ID3D12Resource> resource)
	{
		Name = name;
		_descriptor = descriptor;
		Resource = resource;
		DisposeHeap(ref _rtvHeap);
		DisposeHeap(ref _dsvHeap);
		_rtvHandle = null;
		_dsvHandle = null;
		_srvHandle = DescriptorHandle.Invalid;
		_uavHandle = DescriptorHandle.Invalid;
	}

	public void SetRenderTargetView(ComPtr<ID3D12DescriptorHeap> heap, CpuDescriptorHandle handle)
	{
		DisposeHeap(ref _rtvHeap);
		_rtvHeap = heap;
		_rtvHandle = handle;
	}

	public void SetDepthStencilView(ComPtr<ID3D12DescriptorHeap> heap, CpuDescriptorHandle handle)
	{
		DisposeHeap(ref _dsvHeap);
		_dsvHeap = heap;
		_dsvHandle = handle;
	}

	private static void DisposeHeap(ref ComPtr<ID3D12DescriptorHeap> heap)
	{
		if (heap.Handle is not null)
		{
			heap.Dispose();
			heap = default;
		}
	}

	public void Dispose()
	{
		DisposeHeap(ref _rtvHeap);
		DisposeHeap(ref _dsvHeap);
		if (Resource.Handle is not null)
		{
			Resource.Dispose();
			Resource = default;
		}

		_rtvHandle = null;
		_dsvHandle = null;
	}
}
