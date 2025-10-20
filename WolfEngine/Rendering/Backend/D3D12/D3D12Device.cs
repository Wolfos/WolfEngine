#nullable enable

using System;
using System.Collections.Generic;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.Maths;
using WolfEngine.Rendering.Abstraction;
using AbstractionViewport = WolfEngine.Rendering.Abstraction.Viewport;
using AbstractionVertexBufferView = WolfEngine.Rendering.Abstraction.VertexBufferView;
using AbstractionIndexBufferView = WolfEngine.Rendering.Abstraction.IndexBufferView;
using AbstractionDrawArguments = WolfEngine.Rendering.Abstraction.DrawArguments;
using D3D12Api = Silk.NET.Direct3D12.D3D12;
using Silk.NET.DXGI;

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
		ComPtr<ID3D12CommandQueue> graphicsQueue,
		ComPtr<ID3D12CommandQueue>? computeQueue = null)
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

	private sealed class D3D12CommandList : ID3D12BackendCommandList, IDisposable
	{
		private bool _isClosed;

		public D3D12CommandList(CommandListType type, ComPtr<ID3D12CommandAllocator> allocator,
			ComPtr<ID3D12GraphicsCommandList> commandList)
		{
			Type = type;
			Allocator = allocator;
			CommandList = commandList;
		}

		public CommandListType Type { get; }

		public ComPtr<ID3D12CommandAllocator> Allocator { get; }

		public ComPtr<ID3D12GraphicsCommandList> CommandList { get; }

		public ID3D12GraphicsCommandList* NativeCommandList => CommandList.Handle;

		public void Close()
		{
			if (_isClosed)
			{
				return;
			}

			SilkMarshal.ThrowHResult(CommandList.Close());
			_isClosed = true;
		}

		public void Dispose()
		{
			CommandList.Dispose();
			Allocator.Dispose();
		}

		public void BeginPass(in PassTargets targets, in AbstractionViewport viewport)
		{
			var nativeViewport = new Silk.NET.Direct3D12.Viewport
			{
				TopLeftX = viewport.X,
				TopLeftY = viewport.Y,
				Width = viewport.Width,
				Height = viewport.Height,
				MinDepth = viewport.MinDepth,
				MaxDepth = viewport.MaxDepth
			};

			CommandList.RSSetViewports(1, &nativeViewport);

			var left = (int)Math.Floor(viewport.X);
			var top = (int)Math.Floor(viewport.Y);
			var right = (int)Math.Ceiling(viewport.X + viewport.Width);
			var bottom = (int)Math.Ceiling(viewport.Y + viewport.Height);
			var scissor = new Box2D<int>(left, top, right, bottom);
			CommandList.RSSetScissorRects(1, &scissor);

			var colorCount = targets.ColorAttachments.Count;
			CpuDescriptorHandle* dsvHandle = null;
			CpuDescriptorHandle depthStorage = default;
			if (targets.DepthAttachment is DepthTargetBinding depthBinding)
			{
				if (depthBinding.Texture is not ID3D12BackendTexture depthTexture ||
				    depthTexture.DepthStencilView is null)
				{
					throw new InvalidOperationException("Depth attachment was not provided by the Direct3D12 backend.");
				}

				depthStorage = depthTexture.DepthStencilView.Value;
				dsvHandle = &depthStorage;
			}

			var singleHandle = new Bool32(0);
			if (colorCount > 0)
			{
				Span<CpuDescriptorHandle> rtvSpan = stackalloc CpuDescriptorHandle[colorCount];
				for (var i = 0; i < colorCount; i++)
				{
					if (targets.ColorAttachments[i].Texture is not ID3D12BackendTexture texture ||
					    texture.RenderTargetView is null)
					{
						throw new InvalidOperationException(
							"Render target attachment was not provided by the Direct3D12 backend.");
					}

					rtvSpan[i] = texture.RenderTargetView.Value;
				}

				fixed (CpuDescriptorHandle* rtvPtr = rtvSpan)
				{
					CommandList.OMSetRenderTargets((uint)colorCount, rtvPtr, singleHandle, dsvHandle);
				}

				return;
			}

			CommandList.OMSetRenderTargets(0, (CpuDescriptorHandle*)null, singleHandle, dsvHandle);
		}

		public void EndPass()
		{
			// No-op for now. The application is responsible for inserting any necessary barriers.
		}

		public void BindPipeline(IGfxPipeline pipeline)
		{
			if (pipeline is not D3D12Pipeline nativePipeline)
			{
				throw new InvalidOperationException("Pipeline was not created by the Direct3D12 backend.");
			}

			CommandList.SetPipelineState(nativePipeline.PipelineState.Handle);

			if (nativePipeline.Kind == PassKind.Graphics)
			{
				CommandList.SetGraphicsRootSignature(nativePipeline.RootSignature.Handle);
			}
			else
			{
				CommandList.SetComputeRootSignature(nativePipeline.RootSignature.Handle);
			}
		}

		public void SetBindlessTable(IGfxDescriptorTable table)
		{
			throw new NotSupportedException(
				"Bindless descriptor tables are not yet implemented for the Direct3D12 backend.");
		}

		public void PushConstants<T>(in T data) where T : unmanaged
		{
			throw new NotSupportedException("PushConstants is not yet implemented for the Direct3D12 backend.");
		}

		public void SetVertexBuffers(ReadOnlySpan<AbstractionVertexBufferView> vertexBuffers)
		{
			if (vertexBuffers.IsEmpty)
			{
				return;
			}

			var views = stackalloc Silk.NET.Direct3D12.VertexBufferView[vertexBuffers.Length];

			for (var i = 0; i < vertexBuffers.Length; i++)
			{
				var view = vertexBuffers[i];

				if (view.Buffer is not D3D12Buffer buffer)
				{
					throw new InvalidOperationException("Vertex buffer was not created by the Direct3D12 backend.");
				}

				var resource = buffer.Resource.Handle;
				if (resource is null)
				{
					throw new InvalidOperationException("Vertex buffer resource was null.");
				}

				var gpuAddress = resource->GetGPUVirtualAddress();
				var size = buffer.SizeInBytes;
				var start = view.Offset;
				if (start >= size)
				{
					throw new ArgumentOutOfRangeException(nameof(vertexBuffers),
						"Vertex buffer offset exceeds buffer size.");
				}

				var remaining = size - start;
				var spanSize = Math.Min(remaining, uint.MaxValue);

				views[i] = new Silk.NET.Direct3D12.VertexBufferView
				{
					BufferLocation = gpuAddress + start,
					StrideInBytes = view.Stride,
					SizeInBytes = (uint)spanSize
				};
			}

			CommandList.IASetVertexBuffers(0, (uint)vertexBuffers.Length, views);
		}

		public void SetIndexBuffer(in AbstractionIndexBufferView indexBuffer)
		{
			if (indexBuffer.Buffer is not D3D12Buffer buffer)
			{
				throw new InvalidOperationException("Index buffer was not created by the Direct3D12 backend.");
			}

			var resource = buffer.Resource.Handle;
			if (resource is null)
			{
				throw new InvalidOperationException("Index buffer resource was null.");
			}

			var gpuAddress = resource->GetGPUVirtualAddress();
			var size = buffer.SizeInBytes;
			var offset = indexBuffer.Offset;
			if (offset >= size)
			{
				throw new ArgumentOutOfRangeException(nameof(indexBuffer), "Index buffer offset exceeds buffer size.");
			}

			var remaining = size - offset;
			var spanSize = Math.Min(remaining, uint.MaxValue);

			var format = indexBuffer.Format switch
			{
				IndexFormat.UInt16 => Format.FormatR16Uint,
				IndexFormat.UInt32 => Format.FormatR32Uint,
				_ => throw new ArgumentOutOfRangeException(nameof(indexBuffer), indexBuffer.Format,
					"Unsupported index format.")
			};

			var view = new Silk.NET.Direct3D12.IndexBufferView
			{
				BufferLocation = gpuAddress + offset,
				SizeInBytes = (uint)spanSize,
				Format = format
			};

			CommandList.IASetIndexBuffer(&view);
		}

		public void Draw(in AbstractionDrawArguments arguments)
		{
			CommandList.DrawIndexedInstanced(
				arguments.IndexCount,
				arguments.InstanceCount,
				arguments.StartIndex,
				arguments.BaseVertex,
				arguments.StartInstance);
		}

		public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
		{
			CommandList.Dispatch(groupCountX, groupCountY, groupCountZ);
		}

		public void Barrier(in ResourceBarrierDescription barrier)
		{
			ID3D12Resource* resource = null;
			if (barrier.Resource is ID3D12BackendTexture texture)
			{
				resource = texture.Resource;
			}
			else if (barrier.Resource is D3D12Buffer buffer)
			{
				resource = buffer.Resource.Handle;
			}
			else
			{
				throw new InvalidOperationException("Resource barrier targeted an unsupported resource type.");
			}

			if (resource is null)
			{
				throw new InvalidOperationException("Resource pointer was null while issuing a barrier.");
			}

			var before = ConvertResourceState(barrier.Before);
			var after = ConvertResourceState(barrier.After);
			if (before == after)
			{
				return;
			}

			var transition = new ResourceTransitionBarrier
			{
				PResource = resource,
				Subresource = D3D12Api.ResourceBarrierAllSubresources,
				StateBefore = before,
				StateAfter = after
			};

			var native = new ResourceBarrier
			{
				Type = ResourceBarrierType.Transition,
				Flags = ResourceBarrierFlags.None
			};
			native.Anonymous.Transition = transition;

			CommandList.ResourceBarrier(1, &native);
		}
	}

	private sealed class D3D12Buffer : IGfxBuffer
	{
		private readonly BufferDescriptor _descriptor;

		public D3D12Buffer(string? name, BufferDescriptor descriptor, ComPtr<ID3D12Resource> resource,
			ulong sizeInBytes)
		{
			Name = name;
			_descriptor = descriptor;
			Resource = resource;
			SizeInBytes = sizeInBytes;
		}

		public string? Name { get; }

		public BufferDescriptor Descriptor => _descriptor;

		public ComPtr<ID3D12Resource> Resource { get; private set; }

		public ulong SizeInBytes { get; }
	}

	internal sealed class D3D12Texture : ID3D12BackendTexture, IDisposable
	{
		private readonly TextureDescriptor _descriptor;
		private ComPtr<ID3D12DescriptorHeap> _rtvHeap;
		private ComPtr<ID3D12DescriptorHeap> _dsvHeap;
		private CpuDescriptorHandle? _rtvHandle;
		private CpuDescriptorHandle? _dsvHandle;

		public D3D12Texture(string? name, TextureDescriptor descriptor, ComPtr<ID3D12Resource> resource)
		{
			Name = name;
			_descriptor = descriptor;
			Resource = resource;
		}

		public string? Name { get; }

		public TextureDescriptor Descriptor => _descriptor;

		public ComPtr<ID3D12Resource> Resource { get; private set; }

		public CpuDescriptorHandle? RenderTargetView => _rtvHandle;

		public CpuDescriptorHandle? DepthStencilView => _dsvHandle;

		ID3D12Resource* ID3D12BackendTexture.Resource => Resource.Handle;

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

	private sealed class D3D12Pipeline : IGfxPipeline
	{
		public D3D12Pipeline(PipelineKey key, PassKind kind, ComPtr<ID3D12PipelineState> pipelineState,
			ComPtr<ID3D12RootSignature> rootSignature)
		{
			Key = key;
			Kind = kind;
			PipelineState = pipelineState;
			RootSignature = rootSignature;
		}

		public string? Name => null;

		public PipelineKey Key { get; }

		public PassKind Kind { get; }

		public ComPtr<ID3D12PipelineState> PipelineState { get; }

		public ComPtr<ID3D12RootSignature> RootSignature { get; }
	}

	private static ResourceStates ConvertResourceState(ResourceState state)
	{
		if (state == ResourceState.None)
		{
			return ResourceStates.Common;
		}

		ResourceStates result = 0;

		if ((state & ResourceState.Common) != 0)
		{
			result |= ResourceStates.Common;
		}

		if ((state & ResourceState.RenderTarget) != 0)
		{
			result |= ResourceStates.RenderTarget;
		}

		if ((state & ResourceState.DepthWrite) != 0)
		{
			result |= ResourceStates.DepthWrite;
		}

		if ((state & ResourceState.ShaderResource) != 0)
		{
			result |= ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource;
		}

		if ((state & ResourceState.UnorderedAccess) != 0)
		{
			result |= ResourceStates.UnorderedAccess;
		}

		if ((state & ResourceState.CopySource) != 0)
		{
			result |= ResourceStates.CopySource;
		}

		if ((state & ResourceState.CopyDestination) != 0)
		{
			result |= ResourceStates.CopyDest;
		}

		if ((state & ResourceState.IndirectArgument) != 0)
		{
			result |= ResourceStates.IndirectArgument;
		}

		if ((state & ResourceState.Present) != 0)
		{
			result |= ResourceStates.Present;
		}

		return result == 0 ? ResourceStates.Common : result;
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