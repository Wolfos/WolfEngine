#nullable enable

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using WolfEngine.Rendering.Abstraction;
using Silk.NET.DXGI;
using WolfEngine.Backend.D3D12;


namespace WolfEngine.Rendering.Backend.D3D12;

/// <summary>
/// Placeholder Direct3D12 backend that satisfies the abstraction surface.
/// Provides a staging point for wiring real D3D12 behaviour without blocking compilation.
/// </summary>
public sealed unsafe class D3D12Device : IGfxDevice
{
	private readonly ComPtr<ID3D12Device> _device;
	private readonly ComPtr<ID3D12CommandQueue> _graphicsQueue;
	private readonly ComPtr<ID3D12CommandQueue> _computeQueue;

	private readonly IGfxDescriptorTable _globalTable = new NullDescriptorTable();

	private readonly List<IDisposable> _liveCommandLists = new();
	private readonly object _commandListLock = new();
	

	public D3D12Device(
		ComPtr<ID3D12Device> device,
		ComPtr<ID3D12CommandQueue> graphicsQueue, ComPtr<ID3D12CommandQueue>? computeQueue = null)
	{
		_device = device;
		_graphicsQueue = graphicsQueue;
		_computeQueue = computeQueue ?? graphicsQueue;
	}

	public IGfxCommandList BeginGraphics()
	{
		return CreateCommandList(CommandListType.Direct);
	}

	public IGfxCommandList BeginCompute()
	{
		return CreateCommandList(CommandListType.Compute);
	}

	public void Submit(IGfxCommandList commandList)
	{
		if (commandList is not D3D12CommandList nativeCommandList)
		{
			throw new ArgumentException("Command list was not created by the Direct3D12 backend.", nameof(commandList));
		}

		nativeCommandList.Close();

		ID3D12CommandList* nativeHandle = (ID3D12CommandList*)nativeCommandList.CommandList.Handle;
		var queue = nativeCommandList.Type == CommandListType.Compute ? _computeQueue : _graphicsQueue;

		queue.ExecuteCommandLists(1, &nativeHandle);

		lock (_commandListLock)
		{
			_liveCommandLists.Remove(nativeCommandList);
		}

		nativeCommandList.Dispose();
	}

	public IGfxTexture CreateTexture(in TextureDescriptor descriptor)
	{
		if (descriptor.Width <= 0 || descriptor.Height <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(descriptor), "Textures must have positive dimensions.");
		}

		var format = ToDxgiFormat(descriptor.Format);

		var resourceDesc = new ResourceDesc
		{
			Dimension = ResourceDimension.Texture2D,
			Alignment = 0,
			Width = (ulong)descriptor.Width,
			Height = (uint)descriptor.Height,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = format,
			SampleDesc = new SampleDesc(1, 0),
			Layout = TextureLayout.LayoutUnknown,
			Flags = DetermineResourceFlags(descriptor.Usage)
		};

		var heapProps = new HeapProperties(HeapType.Default);
		var initialState = DetermineInitialState(descriptor.Usage);

		ClearValue clearValue = default;
		ClearValue* clearValuePtr = null;

		if ((descriptor.Usage & TextureUsage.RenderTarget) != 0)
		{
			clearValue.Format = format;
			clearValue.Anonymous.Color[0] = 0.0f;
			clearValue.Anonymous.Color[1] = 0.0f;
			clearValue.Anonymous.Color[2] = 0.0f;
			clearValue.Anonymous.Color[3] = 1.0f;
			clearValuePtr = &clearValue;
		}
		else if ((descriptor.Usage & TextureUsage.DepthStencil) != 0)
		{
			clearValue.Format = format;
			clearValue.Anonymous.DepthStencil = new DepthStencilValue
			{
				Depth = 1.0f,
				Stencil = 0
			};
			clearValuePtr = &clearValue;
		}

		SilkMarshal.ThrowHResult(_device.CreateCommittedResource(
			&heapProps,
			HeapFlags.None,
			in resourceDesc,
			initialState,
			clearValuePtr,
			out ComPtr<ID3D12Resource> resource));

		var texture = new D3D12Texture(null, descriptor, resource);

		if ((descriptor.Usage & TextureUsage.RenderTarget) != 0)
		{
			var rtvHeapDesc = new DescriptorHeapDesc
			{
				Type = DescriptorHeapType.Rtv,
				NumDescriptors = 1,
				Flags = DescriptorHeapFlags.None,
				NodeMask = 0
			};

			SilkMarshal.ThrowHResult(
				_device.CreateDescriptorHeap(in rtvHeapDesc, out ComPtr<ID3D12DescriptorHeap> heap));
			var handle = heap.GetCPUDescriptorHandleForHeapStart();
			_device.CreateRenderTargetView(resource, null, handle);

			texture.SetRenderTargetView(heap, handle);
		}

		if ((descriptor.Usage & TextureUsage.DepthStencil) != 0)
		{
			var dsvHeapDesc = new DescriptorHeapDesc
			{
				Type = DescriptorHeapType.Dsv,
				NumDescriptors = 1,
				Flags = DescriptorHeapFlags.None,
				NodeMask = 0
			};

			SilkMarshal.ThrowHResult(
				_device.CreateDescriptorHeap(in dsvHeapDesc, out ComPtr<ID3D12DescriptorHeap> heap));
			var handle = heap.GetCPUDescriptorHandleForHeapStart();
			_device.CreateDepthStencilView(resource, null, handle);

			texture.SetDepthStencilView(heap, handle);
		}

		return texture;
	}

	public ID3D12BackendTexture ImportExternalTexture(
		in TextureDescriptor descriptor,
		ID3D12Resource* resource,
		CpuDescriptorHandle? rtvHandle,
		CpuDescriptorHandle? dsvHandle)
	{
		if (resource is null)
		{
			throw new ArgumentNullException(nameof(resource));
		}

		return new ExternalD3D12Texture(descriptor, resource, rtvHandle, dsvHandle);
	}

	public IGfxBuffer CreateBuffer(in BufferDescriptor descriptor)
	{
		throw new NotSupportedException("Direct3D12 buffer allocation is not yet implemented.");
	}

	public IGfxPipeline GetOrCreatePipeline(PipelineKey key, in ShaderBytecodeSet shaders)
	{
		throw new NotSupportedException("Direct3D12 pipeline creation is not yet implemented.");
	}

	public IGfxDescriptorTable GlobalTable => _globalTable;
	
	private static ulong Align(ulong size, ulong alignment)
	{
		return (size + alignment - 1) & ~(alignment - 1);
	}
	
	

	private IGfxCommandList CreateCommandList(CommandListType type)
	{
		SilkMarshal.ThrowHResult(_device.CreateCommandAllocator(type, out ComPtr<ID3D12CommandAllocator> allocator));

		SilkMarshal.ThrowHResult(
			_device.CreateCommandList<ID3D12CommandAllocator, ID3D12PipelineState, ID3D12GraphicsCommandList>(
				0,
				type,
				allocator,
				default,
				out ComPtr<ID3D12GraphicsCommandList> commandList));

		var wrapper = new D3D12CommandList(type, allocator, commandList);

		lock (_commandListLock)
		{
			_liveCommandLists.Add(wrapper);
		}

		return wrapper;
	}

	private sealed class NullDescriptorTable : IGfxDescriptorTable
	{
		public DescriptorHandle AllocateShaderResourceView(IGfxResource resource)
		{
			throw new NotSupportedException(
				"Bindless descriptor allocation is not yet implemented for the Direct3D12 backend.");
		}

		public DescriptorHandle AllocateUnorderedAccessView(IGfxResource resource)
		{
			throw new NotSupportedException(
				"Bindless descriptor allocation is not yet implemented for the Direct3D12 backend.");
		}

		public DescriptorHandle AllocateConstantBufferView(IGfxBuffer buffer)
		{
			throw new NotSupportedException(
				"Bindless descriptor allocation is not yet implemented for the Direct3D12 backend.");
		}

		public DescriptorHandle AllocateSampler(in SamplerDescriptor sampler)
		{
			throw new NotSupportedException(
				"Bindless descriptor allocation is not yet implemented for the Direct3D12 backend.");
		}
	}

	private sealed class ExternalD3D12Texture : ID3D12BackendTexture
	{
		public ExternalD3D12Texture(TextureDescriptor descriptor, ID3D12Resource* resource, CpuDescriptorHandle? rtv,
			CpuDescriptorHandle? dsv)
		{
			Descriptor = descriptor;
			Resource = resource;
			RenderTargetView = rtv;
			DepthStencilView = dsv;
		}

		public string? Name => null;

		public TextureDescriptor Descriptor { get; }

		public ID3D12Resource* Resource { get; }

		public CpuDescriptorHandle? RenderTargetView { get; }

		public CpuDescriptorHandle? DepthStencilView { get; }
	}
	

	private static Format ToDxgiFormat(TextureFormat format) => format switch
	{
		TextureFormat.Bgra8Unorm => Format.FormatB8G8R8A8Unorm,
		TextureFormat.Rgba8Unorm => Format.FormatR8G8B8A8Unorm,
		TextureFormat.Rgba16Float => Format.FormatR16G16B16A16Float,
		TextureFormat.D32Float => Format.FormatD32Float,
		TextureFormat.Unknown => Format.FormatUnknown,
		_ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported texture format.")
	};

	private static ResourceFlags DetermineResourceFlags(TextureUsage usage)
	{
		var flags = ResourceFlags.None;

		if ((usage & TextureUsage.RenderTarget) != 0)
		{
			flags |= ResourceFlags.AllowRenderTarget;
		}

		if ((usage & TextureUsage.DepthStencil) != 0)
		{
			flags |= ResourceFlags.AllowDepthStencil;
		}

		if ((usage & TextureUsage.ShaderResource) == 0)
		{
			flags |= ResourceFlags.DenyShaderResource;
		}

		return flags;
	}

	private static ResourceStates DetermineInitialState(TextureUsage usage)
	{
		if ((usage & TextureUsage.RenderTarget) != 0)
		{
			return ResourceStates.RenderTarget;
		}

		if ((usage & TextureUsage.DepthStencil) != 0)
		{
			return ResourceStates.DepthWrite;
		}

		return ResourceStates.Common;
	}
}
