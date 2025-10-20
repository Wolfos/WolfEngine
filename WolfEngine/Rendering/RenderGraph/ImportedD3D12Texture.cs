#nullable enable

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Backend.D3D12;

namespace WolfEngine.Rendering;

/// <summary>
/// Wraps external D3D12 textures so they can be imported into the render graph via the abstraction layer.
/// </summary>
public sealed unsafe class ImportedD3D12Texture : ID3D12BackendTexture, IDisposable
{
	private ComPtr<ID3D12Resource> _resource;
	private ComPtr<ID3D12DescriptorHeap>? _rtvHeap;
	private ComPtr<ID3D12DescriptorHeap>? _dsvHeap;

	public ImportedD3D12Texture(
		TextureDescriptor descriptor,
		ComPtr<ID3D12Resource> resource,
		ComPtr<ID3D12DescriptorHeap>? rtvHeap,
		CpuDescriptorHandle rtvHandle,
		ComPtr<ID3D12DescriptorHeap>? dsvHeap,
		CpuDescriptorHandle dsvHandle)
	{
		Descriptor = descriptor;
		_resource = resource;
		_rtvHeap = rtvHeap;
		_dsvHeap = dsvHeap;
		RenderTargetView = rtvHeap.HasValue ? rtvHandle : (CpuDescriptorHandle?) null;
		DepthStencilView = dsvHeap.HasValue ? dsvHandle : (CpuDescriptorHandle?) null;
	}

	public string? Name => null;

	public TextureDescriptor Descriptor { get; }

	public ComPtr<ID3D12Resource> Resource => _resource;

	ID3D12Resource* ID3D12BackendTexture.Resource => _resource.Handle;

	public CpuDescriptorHandle? RenderTargetView { get; }

	public CpuDescriptorHandle? DepthStencilView { get; }

	public void Dispose()
	{
		_resource.Dispose();
		_rtvHeap?.Dispose();
		_dsvHeap?.Dispose();
	}
}
