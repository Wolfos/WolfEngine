using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace WolfEngine.Rendering;

public unsafe interface ID3D12RenderGraphTexture : IRenderGraphTexture
{
	ID3D12Resource* Resource { get; }
	bool HasRenderTargetView { get; }
	CpuDescriptorHandle RenderTargetView { get; }
	bool HasDepthStencilView { get; }
	CpuDescriptorHandle DepthStencilView { get; }
}

public sealed unsafe class D3D12RenderGraphBackend : IRenderGraphBackend
{
	private readonly ComPtr<ID3D12Device> _device;

	public D3D12RenderGraphBackend(ComPtr<ID3D12Device> device)
	{
		_device = device;
	}

	public IRenderGraphTexture CreateTexture(in TextureDescriptor descriptor)
	{
		if ((descriptor.Usage & TextureUsage.RenderTarget) != 0)
		{
			return CreateColorTarget(descriptor);
		}

		if ((descriptor.Usage & TextureUsage.DepthStencil) != 0)
		{
			return CreateDepthTarget(descriptor);
		}

		throw new NotSupportedException($"Unsupported texture usage: {descriptor.Usage}");
	}

	private D3D12RenderGraphTexture CreateColorTarget(in TextureDescriptor descriptor)
	{
		var format = ToDxgiFormat(descriptor.Format);
		var textureDesc = new ResourceDesc
		{
			Dimension = ResourceDimension.Texture2D,
			Alignment = 0,
			Width = (ulong) descriptor.Width,
			Height = (uint) descriptor.Height,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = format,
			SampleDesc = new SampleDesc(1, 0),
			Layout = TextureLayout.LayoutUnknown,
			Flags = ResourceFlags.AllowRenderTarget
		};

		var heapProps = new HeapProperties(HeapType.Default);

		var clearColor = new ClearValue
		{
			Format = format
		};
		clearColor.Anonymous.Color[0] = 0.0f;
		clearColor.Anonymous.Color[1] = 0.0f;
		clearColor.Anonymous.Color[2] = 0.0f;
		clearColor.Anonymous.Color[3] = 1.0f;

		SilkMarshal.ThrowHResult(_device.CreateCommittedResource<ID3D12Resource>(
			&heapProps,
			HeapFlags.None,
			in textureDesc,
			ResourceStates.RenderTarget,
			&clearColor,
			out var resource));

		var heapDesc = new DescriptorHeapDesc
		{
			Type = DescriptorHeapType.Rtv,
			NumDescriptors = 1,
			Flags = DescriptorHeapFlags.None,
			NodeMask = 0
		};

		SilkMarshal.ThrowHResult(_device.CreateDescriptorHeap<ID3D12DescriptorHeap>(in heapDesc, out var heap));
		var handle = heap.GetCPUDescriptorHandleForHeapStart();
		_device.CreateRenderTargetView(resource, null, handle);

		return new D3D12RenderGraphTexture(descriptor, resource, heap, handle, default, default, hasRtv: true, hasDsv: false);
	}

	private D3D12RenderGraphTexture CreateDepthTarget(in TextureDescriptor descriptor)
	{
		var format = ToDxgiFormat(descriptor.Format);
		var textureDesc = new ResourceDesc
		{
			Dimension = ResourceDimension.Texture2D,
			Alignment = 0,
			Width = (ulong) descriptor.Width,
			Height = (uint) descriptor.Height,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = format,
			SampleDesc = new SampleDesc(1, 0),
			Layout = TextureLayout.LayoutUnknown,
			Flags = ResourceFlags.AllowDepthStencil
		};

		var heapProps = new HeapProperties(HeapType.Default);

		var clearValue = new ClearValue
		{
			Format = format
		};
		clearValue.Anonymous.DepthStencil = new DepthStencilValue
		{
			Depth = 1.0f,
			Stencil = 0
		};

		SilkMarshal.ThrowHResult(_device.CreateCommittedResource<ID3D12Resource>(
			&heapProps,
			HeapFlags.None,
			in textureDesc,
			ResourceStates.DepthWrite,
			&clearValue,
			out var resource));

		var heapDesc = new DescriptorHeapDesc
		{
			Type = DescriptorHeapType.Dsv,
			NumDescriptors = 1,
			Flags = DescriptorHeapFlags.None,
			NodeMask = 0
		};

		SilkMarshal.ThrowHResult(_device.CreateDescriptorHeap<ID3D12DescriptorHeap>(in heapDesc, out var heap));
		var handle = heap.GetCPUDescriptorHandleForHeapStart();
		_device.CreateDepthStencilView(resource, null, handle);

		return new D3D12RenderGraphTexture(descriptor, resource, default, default, heap, handle, hasRtv: false, hasDsv: true);
	}

	private static Format ToDxgiFormat(TextureFormat format) => format switch
	{
		TextureFormat.Bgra8Unorm => Format.FormatB8G8R8A8Unorm,
		TextureFormat.Rgba8Unorm => Format.FormatR8G8B8A8Unorm,
		TextureFormat.Rgba16Float => Format.FormatR16G16B16A16Float,
		TextureFormat.D32Float => Format.FormatD32Float,
		_ => throw new NotSupportedException($"Unsupported texture format: {format}")
	};
}

public sealed unsafe class D3D12RenderGraphTexture : ID3D12RenderGraphTexture
{
	private ComPtr<ID3D12Resource> _resource;
	private ComPtr<ID3D12DescriptorHeap> _rtvHeap;
	private ComPtr<ID3D12DescriptorHeap> _dsvHeap;
	private readonly CpuDescriptorHandle _rtvHandle;
	private readonly CpuDescriptorHandle _dsvHandle;

	public D3D12RenderGraphTexture(
		TextureDescriptor descriptor,
		ComPtr<ID3D12Resource> resource,
		ComPtr<ID3D12DescriptorHeap> rtvHeap,
		CpuDescriptorHandle rtvHandle,
		ComPtr<ID3D12DescriptorHeap> dsvHeap,
		CpuDescriptorHandle dsvHandle,
		bool hasRtv,
		bool hasDsv)
	{
		Descriptor = descriptor;
		_resource = resource;
		_rtvHeap = rtvHeap;
		_dsvHeap = dsvHeap;
		_rtvHandle = rtvHandle;
		_dsvHandle = dsvHandle;
		HasRenderTargetView = hasRtv;
		HasDepthStencilView = hasDsv;
	}

	public TextureDescriptor Descriptor { get; }

	public ID3D12Resource* Resource => _resource.Handle;

	public bool HasRenderTargetView { get; }

	public CpuDescriptorHandle RenderTargetView => HasRenderTargetView
		? _rtvHandle
		: throw new InvalidOperationException("Texture does not expose an RTV.");

	public bool HasDepthStencilView { get; }

	public CpuDescriptorHandle DepthStencilView => HasDepthStencilView
		? _dsvHandle
		: throw new InvalidOperationException("Texture does not expose a DSV.");

	public void Dispose()
	{
		_resource.Dispose();
		_rtvHeap.Dispose();
		_dsvHeap.Dispose();
	}
}

public sealed unsafe class D3D12ExternalRenderGraphTexture : ID3D12RenderGraphTexture
{
	public D3D12ExternalRenderGraphTexture(
		TextureDescriptor descriptor,
		ID3D12Resource* resource,
		bool hasRtv,
		CpuDescriptorHandle rtvHandle,
		bool hasDsv,
		CpuDescriptorHandle dsvHandle)
	{
		Descriptor = descriptor;
		Resource = resource;
		HasRenderTargetView = hasRtv;
		_renderTargetView = rtvHandle;
		HasDepthStencilView = hasDsv;
		_depthStencilView = dsvHandle;
	}

	private readonly CpuDescriptorHandle _renderTargetView;
	private readonly CpuDescriptorHandle _depthStencilView;

	public TextureDescriptor Descriptor { get; }

	public ID3D12Resource* Resource { get; }

	public bool HasRenderTargetView { get; }

	public CpuDescriptorHandle RenderTargetView => _renderTargetView;

	public bool HasDepthStencilView { get; }

	public CpuDescriptorHandle DepthStencilView => _depthStencilView;

	public void Dispose()
	{
		// External textures are owned elsewhere.
	}
}
