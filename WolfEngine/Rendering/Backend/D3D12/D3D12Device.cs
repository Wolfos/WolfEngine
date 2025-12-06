#nullable enable

using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using WolfEngine.Rendering.Abstraction;
using Silk.NET.DXGI;
using WolfEngine.Backend.D3D12;
using AbstractionFillMode = WolfEngine.Rendering.Abstraction.FillMode;
using AbstractionCullMode = WolfEngine.Rendering.Abstraction.CullMode;
using D3D12Api = Silk.NET.Direct3D12.D3D12;
using D3DFillMode = Silk.NET.Direct3D12.FillMode;
using D3DCullMode = Silk.NET.Direct3D12.CullMode;

using FenceFlags = Silk.NET.Direct3D12.FenceFlags;
using Fence = Silk.NET.Direct3D12.ID3D12Fence;

namespace WolfEngine.Rendering.Backend.D3D12;

/// <summary>
/// Placeholder Direct3D12 backend that satisfies the abstraction surface.
/// Provides a staging point for wiring real D3D12 behaviour without blocking compilation.
/// </summary>
public sealed unsafe class D3D12Device : IGfxDevice, ITexturePoolDevice
{
	private readonly ComPtr<ID3D12Device> _device;
	private readonly ComPtr<ID3D12CommandQueue> _graphicsQueue;
	private readonly ComPtr<ID3D12CommandQueue> _computeQueue;
	private readonly D3D12Api _d3d12 = D3D12Api.GetApi();

	private readonly IGfxDescriptorTable _globalTable = new NullDescriptorTable();

	private readonly List<CommandListSubmission> _inFlightCommandLists = new();
	private readonly object _commandListLock = new();
	private readonly ComPtr<Fence> _submissionFence;
	private ulong _submissionFenceValue;
	
	private readonly Dictionary<PipelineKey, IGfxPipeline> _pipelineCache = new();
	private readonly object _pipelineLock = new();
	private readonly Dictionary<GraphicsLayoutKind, ComPtr<ID3D12RootSignature>> _graphicsRootSignatures = new();
	private ComPtr<ID3D12RootSignature> _computeRootSignature;
	private readonly Dictionary<CommandListType, Queue<D3D12CommandList>> _commandListPool = new();
	private readonly Dictionary<TextureDescriptor, Queue<PooledTexture>> _texturePool = new(new TextureDescriptorComparer());
	private readonly Queue<ExternalD3D12Texture> _externalTexturePool = new();
	private readonly object _texturePoolLock = new();
	private ComPtr<ID3D12CommandAllocator> _transitionAllocator;
	private ComPtr<ID3D12GraphicsCommandList> _transitionCommandList;
	private nint _submissionFenceEvent;

	private readonly struct PooledTexture
	{
		public PooledTexture(D3D12Texture texture, ResourceStates state)
		{
			Texture = texture;
			LastKnownState = state;
		}

		public D3D12Texture Texture { get; }

		public ResourceStates LastKnownState { get; }
	}

	private sealed class TextureDescriptorComparer : IEqualityComparer<TextureDescriptor>
	{
		public bool Equals(TextureDescriptor x, TextureDescriptor y)
		{
			return x.Width == y.Width &&
			       x.Height == y.Height &&
			       x.Format == y.Format &&
			       x.Usage == y.Usage &&
			       x.ClearColor.Equals(y.ClearColor) &&
			       Math.Abs(x.DepthClear - y.DepthClear) < float.Epsilon;
		}

		public int GetHashCode(TextureDescriptor obj)
		{
			return HashCode.Combine(
				obj.Width,
				obj.Height,
				(int)obj.Format,
				(int)obj.Usage,
				obj.ClearColor,
				obj.DepthClear);
		}
	}
	
	private readonly struct CommandListSubmission
	{
		public CommandListSubmission(D3D12CommandList commandList, ulong fenceValue)
		{
			CommandList = commandList;
			FenceValue = fenceValue;
		}

		public D3D12CommandList CommandList { get; }

		public ulong FenceValue { get; }
	}

	public D3D12Device(
		ComPtr<ID3D12Device> device,
		ComPtr<ID3D12CommandQueue> graphicsQueue, ComPtr<ID3D12CommandQueue>? computeQueue = null)
	{
		_device = device;
		_graphicsQueue = graphicsQueue;
		_computeQueue = computeQueue ?? graphicsQueue;
		SilkMarshal.ThrowHResult(_device.CreateFence(0, FenceFlags.None, out _submissionFence));
		_submissionFenceValue = 0;
	}

	public IGfxCommandList BeginGraphics()
	{
		return CreateCommandList(CommandListType.Direct);
	}

	public IGfxCommandList BeginCompute()
	{
		return CreateCommandList(CommandListType.Direct);
	}

	public void Submit(IGfxCommandList commandList)
	{
		if (commandList is not D3D12CommandList nativeCommandList)
		{
			throw new ArgumentException("Command list was not created by the Direct3D12 backend.", nameof(commandList));
		}

		nativeCommandList.Close();

		var nativeHandle = (ID3D12CommandList*)nativeCommandList.CommandList.Handle;
		var queue = nativeCommandList.Type == CommandListType.Compute ? _computeQueue : _graphicsQueue;

		queue.ExecuteCommandLists(1, &nativeHandle);
		var fenceValue = ++_submissionFenceValue;
		SilkMarshal.ThrowHResult(queue.Signal(_submissionFence, fenceValue));

		lock (_commandListLock)
		{
			_inFlightCommandLists.Add(new(nativeCommandList, fenceValue));
			CleanupCompletedCommandListsLocked();
		}
	}

	public IGfxTexture CreateTexture(in TextureDescriptor descriptor)
	{
		if (descriptor.Width <= 0 || descriptor.Height <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(descriptor), "Textures must have positive dimensions.");
		}

		var initialState = DetermineInitialState(descriptor.Usage);
		if (TryRentPooledTexture(descriptor, initialState, out var pooledTexture))
		{
			return pooledTexture;
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

		ClearValue clearValue = default;
		ClearValue* clearValuePtr = null;

		if ((descriptor.Usage & TextureUsage.RenderTarget) != 0)
		{
			clearValue.Format = format;
			clearValue.Anonymous.Color[0] = descriptor.ClearColor.X;
			clearValue.Anonymous.Color[1] = descriptor.ClearColor.Y;
			clearValue.Anonymous.Color[2] = descriptor.ClearColor.Z;
			clearValue.Anonymous.Color[3] = descriptor.ClearColor.W;
			clearValuePtr = &clearValue;
		}
		else if ((descriptor.Usage & TextureUsage.DepthStencil) != 0)
		{
			clearValue.Format = format;
			clearValue.Anonymous.DepthStencil = new DepthStencilValue
			{
				Depth = descriptor.DepthClear,
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

		var texture = new D3D12Texture();
		texture.Initialize(null, descriptor, resource);

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

		var wrapper = RentExternalTextureWrapper();
		wrapper.Initialize(descriptor, resource, rtvHandle, dsvHandle);
		return wrapper;
	}

	public IGfxBuffer CreateBuffer(in BufferDescriptor descriptor)
	{
		throw new NotSupportedException("Direct3D12 buffer allocation is not yet implemented.");
	}

	public IGfxPipeline GetOrCreatePipeline(PipelineKey key, in ShaderBytecodeSet shaders)
	{
		lock (_pipelineLock)
		{
			if (_pipelineCache.TryGetValue(key, out var cached))
			{
				return cached;
			}

			IGfxPipeline pipeline = key.PassKind switch
			{
				PassKind.Graphics => CreateGraphicsPipeline(key, shaders),
				PassKind.Compute => CreateComputePipeline(key, shaders),
				_ => throw new NotSupportedException($"Unsupported pass kind '{key.PassKind}'.")
			};

			_pipelineCache[key] = pipeline;
			return pipeline;
		}
	}

	public IGfxDescriptorSetBuilder CreateDescriptorSetBuilder()
	{
		return new D3D12DescriptorSetBuilder(_device);
	}

	public IGfxDescriptorTable GlobalTable => _globalTable;
	
	private static ulong Align(ulong size, ulong alignment)
	{
		return (size + alignment - 1) & ~(alignment - 1);
	}
	
	

	private IGfxCommandList CreateCommandList(CommandListType type)
	{
		if (_commandListPool.TryGetValue(type, out var queue) && queue.Count > 0)
		{
			var pooled = queue.Dequeue();
			pooled.Reset();
			return pooled;
		}

		SilkMarshal.ThrowHResult(_device.CreateCommandAllocator(type, out ComPtr<ID3D12CommandAllocator> allocator));

		SilkMarshal.ThrowHResult(
			_device.CreateCommandList<ID3D12CommandAllocator, ID3D12PipelineState, ID3D12GraphicsCommandList>(
				0,
				type,
				allocator,
				default,
				out ComPtr<ID3D12GraphicsCommandList> commandList));

		var wrapper = new D3D12CommandList(type, allocator, commandList);

		return wrapper;
	}

	private bool TryRentPooledTexture(in TextureDescriptor descriptor, ResourceStates desiredState, out IGfxTexture texture)
	{
		PooledTexture? pooled = null;

		lock (_texturePoolLock)
		{
			if (_texturePool.TryGetValue(descriptor, out var queue) && queue.Count > 0)
			{
				pooled = queue.Dequeue();
			}
		}

		if (pooled.HasValue == false)
		{
			texture = null!;
			return false;
		}

		if (pooled.Value.LastKnownState != desiredState)
		{
			TransitionResource(pooled.Value.Texture.Resource.Handle, pooled.Value.LastKnownState, desiredState);
		}

		texture = pooled.Value.Texture;
		return true;
	}

	private ExternalD3D12Texture RentExternalTextureWrapper()
	{
		lock (_texturePoolLock)
		{
			if (_externalTexturePool.Count > 0)
			{
				return _externalTexturePool.Dequeue();
			}
		}

		return new ExternalD3D12Texture();
	}

	private void CleanupCompletedCommandListsLocked()
	{
		var completedFence = _submissionFence.Handle->GetCompletedValue();
		for (var i = _inFlightCommandLists.Count - 1; i >= 0; i--)
		{
			if (_inFlightCommandLists[i].FenceValue <= completedFence)
			{
				var completed = _inFlightCommandLists[i].CommandList;
				if (_commandListPool.TryGetValue(completed.Type, out var pool) == false)
				{
					pool = new Queue<D3D12CommandList>();
					_commandListPool[completed.Type] = pool;
				}

				pool.Enqueue(completed);
				_inFlightCommandLists.RemoveAt(i);
			}
		}
	}

	public bool ReturnTexture(IGfxTexture texture, ResourceState lastKnownState)
	{
		switch (texture)
		{
			case D3D12Texture owned:
				var state = ToBackendState(lastKnownState);
				lock (_texturePoolLock)
				{
					if (_texturePool.TryGetValue(owned.Descriptor, out var queue) == false)
					{
						queue = new Queue<PooledTexture>();
						_texturePool[owned.Descriptor] = queue;
					}

					queue.Enqueue(new PooledTexture(owned, state));
				}

				return true;
			case ExternalD3D12Texture external:
				external.Reset();
				lock (_texturePoolLock)
				{
					_externalTexturePool.Enqueue(external);
				}

				return true;
			default:
				return false;
		}
	}

	public void ClearTexturePool()
	{
		lock (_texturePoolLock)
		{
			foreach (var queue in _texturePool.Values)
			{
				while (queue.Count > 0)
				{
					queue.Dequeue().Texture.Dispose();
				}
			}

			_texturePool.Clear();

			_externalTexturePool.Clear();
		}
	}

	private static ResourceStates ToBackendState(ResourceState state)
	{
		var result = ResourceStates.Common;

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

		return result;
	}

	private void TransitionResource(ID3D12Resource* resource, ResourceStates before, ResourceStates after)
	{
		if (before == after)
		{
			return;
		}

		EnsureTransitionCommandList();

		SilkMarshal.ThrowHResult(_transitionAllocator.Reset());
		SilkMarshal.ThrowHResult(_transitionCommandList.Reset(_transitionAllocator, (ID3D12PipelineState*) null));

		var barrier = new ResourceBarrier {Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None};
		barrier.Anonymous.Transition = new()
		{
			PResource = resource,
			Subresource = D3D12Api.ResourceBarrierAllSubresources,
			StateBefore = before,
			StateAfter = after
		};

		_transitionCommandList.ResourceBarrier(1, &barrier);
		SilkMarshal.ThrowHResult(_transitionCommandList.Close());
		ID3D12CommandList* lists = (ID3D12CommandList*) _transitionCommandList.Handle;
		_graphicsQueue.ExecuteCommandLists(1, &lists);

		var fenceValue = ++_submissionFenceValue;
		SilkMarshal.ThrowHResult(_graphicsQueue.Signal(_submissionFence, fenceValue));
		WaitForFence(fenceValue);
	}

	private void EnsureTransitionCommandList()
	{
		if (_transitionCommandList.Handle is not null)
		{
			return;
		}

		SilkMarshal.ThrowHResult(_device.CreateCommandAllocator(CommandListType.Direct, out _transitionAllocator));
		SilkMarshal.ThrowHResult(
			_device.CreateCommandList<ID3D12CommandAllocator, ID3D12PipelineState, ID3D12GraphicsCommandList>(
				0,
				CommandListType.Direct,
				_transitionAllocator,
				default,
				out _transitionCommandList));

		// Command lists are created in the recording state; close once so later Reset calls succeed.
		SilkMarshal.ThrowHResult(_transitionCommandList.Close());
	}

	private void WaitForFence(ulong fenceValue)
	{
		if (_submissionFence.Handle->GetCompletedValue() >= fenceValue)
		{
			return;
		}

		EnsureSubmissionFenceEvent();
		SilkMarshal.ThrowHResult(_submissionFence.Handle->SetEventOnCompletion(fenceValue, (void*) _submissionFenceEvent));
		WaitForSingleObject(_submissionFenceEvent, 0xFFFFFFFF);
	}

	private void EnsureSubmissionFenceEvent()
	{
		if (_submissionFenceEvent != nint.Zero)
		{
			return;
		}

		_submissionFenceEvent = CreateEventEx(nint.Zero, null, 0, 0x1F0003);
		if (_submissionFenceEvent == nint.Zero)
		{
			throw new InvalidOperationException("Failed to create submission fence event.");
		}
	}

	private D3D12Pipeline CreateGraphicsPipeline(PipelineKey key, in ShaderBytecodeSet shaders)
	{
		if (shaders.Vertex.HasValue == false || shaders.Pixel.HasValue == false)
		{
			throw new InvalidOperationException("Graphics pipelines require both vertex and pixel shaders.");
		}

		var rootSignature = EnsureGraphicsRootSignature(key.Layout);

		var vertexShader = shaders.Vertex.Value.Span;
		var pixelShader = shaders.Pixel.Value.Span;

		Span<byte> positionSemantic = stackalloc byte["POSITION".Length + 1];
		Span<byte> normalSemantic = stackalloc byte["NORMAL".Length + 1];
		Span<byte> texCoordSemantic = stackalloc byte["TEXCOORD".Length + 1];
		Span<byte> tangentSemantic = stackalloc byte["TANGENT".Length + 1];
		CopySemantic("POSITION"u8, positionSemantic);
		CopySemantic("NORMAL"u8, normalSemantic);
		CopySemantic("TEXCOORD"u8, texCoordSemantic);
		CopySemantic("TANGENT"u8, tangentSemantic);

		var inputElements = stackalloc InputElementDesc[4];
		fixed (byte* positionPtr = positionSemantic)
		fixed (byte* normalPtr = normalSemantic)
		fixed (byte* texCoordPtr = texCoordSemantic)
		fixed (byte* tangentPtr = tangentSemantic)
		{
			inputElements[0] = default;
			inputElements[0].SemanticName = positionPtr;
			inputElements[0].SemanticIndex = 0;
			inputElements[0].Format = Format.FormatR32G32B32A32Float;
			inputElements[0].InputSlot = 0;
			inputElements[0].AlignedByteOffset = 0;
			inputElements[0].InputSlotClass = InputClassification.PerVertexData;
			inputElements[0].InstanceDataStepRate = 0;

			inputElements[1] = default;
			inputElements[1].SemanticName = normalPtr;
			inputElements[1].SemanticIndex = 0;
			inputElements[1].Format = Format.FormatR32G32B32Float;
			inputElements[1].InputSlot = 0;
			inputElements[1].AlignedByteOffset = 16;
			inputElements[1].InputSlotClass = InputClassification.PerVertexData;
			inputElements[1].InstanceDataStepRate = 0;

			inputElements[2] = default;
			inputElements[2].SemanticName = texCoordPtr;
			inputElements[2].SemanticIndex = 0;
			inputElements[2].Format = Format.FormatR32G32Float;
			inputElements[2].InputSlot = 0;
			inputElements[2].AlignedByteOffset = 32;
			inputElements[2].InputSlotClass = InputClassification.PerVertexData;
			inputElements[2].InstanceDataStepRate = 0;

			inputElements[3] = default;
			inputElements[3].SemanticName = tangentPtr;
			inputElements[3].SemanticIndex = 0;
			inputElements[3].Format = Format.FormatR32G32B32A32Float;
			inputElements[3].InputSlot = 0;
			inputElements[3].AlignedByteOffset = 40;
			inputElements[3].InputSlotClass = InputClassification.PerVertexData;
			inputElements[3].InstanceDataStepRate = 0;

			var inputLayout = new InputLayoutDesc
			{
				PInputElementDescs = inputElements,
				NumElements = 4
			};

			var renderState = CreateNormalizedRenderState(key.RenderState);
			var blendState = CreateBlendState(renderState, key.RenderTargets.Formats.Span.Length);
			var rasterizerState = CreateRasterizerState(renderState);
			var depthStencilState = CreateDepthStencilState(renderState);

			var targetFormats = key.RenderTargets.Formats.Span;
			var depthFormat = key.DepthStencil.Format != TextureFormat.Unknown
				? ToDxgiFormat(key.DepthStencil.Format)
				: Format.FormatUnknown;

			fixed (byte* vertexPtr = vertexShader)
			fixed (byte* pixelPtr = pixelShader)
			{
				var shaderBytecodeVS = new ShaderBytecode
				{
					PShaderBytecode = vertexPtr,
					BytecodeLength = (nuint) vertexShader.Length
				};

				var shaderBytecodePS = new ShaderBytecode
				{
					PShaderBytecode = pixelPtr,
					BytecodeLength = (nuint) pixelShader.Length
				};

				var psoDesc = new GraphicsPipelineStateDesc
				{
					PRootSignature = rootSignature.Handle,
					VS = shaderBytecodeVS,
					PS = shaderBytecodePS,
					BlendState = blendState,
					SampleMask = D3D12Api.DefaultSampleMask,
					RasterizerState = rasterizerState,
					DepthStencilState = depthStencilState,
					InputLayout = inputLayout,
					IBStripCutValue = IndexBufferStripCutValue.ValueDisabled,
					PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
					NumRenderTargets = (uint) targetFormats.Length,
					DSVFormat = depthFormat,
					SampleDesc = new SampleDesc(1, 0),
					NodeMask = 0,
					CachedPSO = default,
					Flags = PipelineStateFlags.None
				};

				for (var i = 0; i < targetFormats.Length; i++)
				{
					psoDesc.RTVFormats[i] = ToDxgiFormat(targetFormats[i]);
				}

				SilkMarshal.ThrowHResult(
					_device.CreateGraphicsPipelineState(in psoDesc, out ComPtr<ID3D12PipelineState> pipelineState));

				return new D3D12Pipeline(key, PassKind.Graphics, pipelineState, rootSignature);
			}
		}
	}

	private D3D12Pipeline CreateComputePipeline(PipelineKey key, in ShaderBytecodeSet shaders)
	{
		if (shaders.Compute.HasValue == false)
		{
			throw new InvalidOperationException("Compute pipelines require a compute shader.");
		}

		EnsureComputeRootSignature();

		var computeShader = shaders.Compute.Value.Span;
		fixed (byte* shaderPtr = computeShader)
		{
			var shaderBytecode = new ShaderBytecode
			{
				PShaderBytecode = shaderPtr,
				BytecodeLength = (nuint) computeShader.Length
			};

			var pipelineDesc = new ComputePipelineStateDesc
			{
				PRootSignature = _computeRootSignature.Handle,
				CS = shaderBytecode,
				NodeMask = 0,
				CachedPSO = default,
				Flags = PipelineStateFlags.None
			};

			SilkMarshal.ThrowHResult(
				_device.CreateComputePipelineState(in pipelineDesc, out ComPtr<ID3D12PipelineState> pipelineState));

		return new D3D12Pipeline(key, PassKind.Compute, pipelineState, _computeRootSignature);
	}
	}

	private ComPtr<ID3D12RootSignature> EnsureGraphicsRootSignature(GraphicsLayoutKind layout)
	{
		if (_graphicsRootSignatures.TryGetValue(layout, out var existing) && existing.Handle is not null)
		{
			return existing;
		}

		var ranges = stackalloc DescriptorRange[1];
		var rootParameters = stackalloc RootParameter[4];
		var staticSampler = stackalloc StaticSamplerDesc[1];

		switch (layout)
		{
			case GraphicsLayoutKind.Material:
			case GraphicsLayoutKind.Default:
			{
				ranges[0].RangeType = DescriptorRangeType.Srv;
				ranges[0].NumDescriptors = 5;
				ranges[0].BaseShaderRegister = 0;
				ranges[0].RegisterSpace = 0;
				ranges[0].OffsetInDescriptorsFromTableStart = 0;

				rootParameters[0].ParameterType = RootParameterType.TypeCbv;
				rootParameters[0].Anonymous.Descriptor = new()
				{
					ShaderRegister = 0,
					RegisterSpace = 0
				};
				rootParameters[0].ShaderVisibility = ShaderVisibility.Pixel;

				rootParameters[1].ParameterType = RootParameterType.Type32BitConstants;
				rootParameters[1].Anonymous.Constants = new()
				{
					ShaderRegister = 1,
					RegisterSpace = 0,
					Num32BitValues = 16
				};
				rootParameters[1].ShaderVisibility = ShaderVisibility.Vertex;

				rootParameters[2].ParameterType = RootParameterType.Type32BitConstants;
				rootParameters[2].Anonymous.Constants = new()
				{
					ShaderRegister = 2,
					RegisterSpace = 0,
					Num32BitValues = 20
				};
				rootParameters[2].ShaderVisibility = ShaderVisibility.All;

				rootParameters[3].ParameterType = RootParameterType.TypeDescriptorTable;
				rootParameters[3].Anonymous.DescriptorTable.NumDescriptorRanges = 1;
				rootParameters[3].Anonymous.DescriptorTable.PDescriptorRanges = ranges;
				rootParameters[3].ShaderVisibility = ShaderVisibility.Pixel;

				staticSampler[0] = new()
				{
					Filter = Filter.MinMagMipLinear,
					AddressU = TextureAddressMode.Wrap,
					AddressV = TextureAddressMode.Wrap,
					AddressW = TextureAddressMode.Wrap,
					MipLODBias = 0.0f,
					MaxAnisotropy = 0,
					ComparisonFunc = ComparisonFunc.Always,
					BorderColor = StaticBorderColor.OpaqueWhite,
					MinLOD = 0.0f,
					MaxLOD = float.MaxValue,
					ShaderRegister = 0,
					RegisterSpace = 0,
					ShaderVisibility = ShaderVisibility.Pixel
				};
				break;
			}
			case GraphicsLayoutKind.Skybox:
			{
				ranges[0].RangeType = DescriptorRangeType.Srv;
				ranges[0].NumDescriptors = 1;
				ranges[0].BaseShaderRegister = 0;
				ranges[0].RegisterSpace = 0;
				ranges[0].OffsetInDescriptorsFromTableStart = 0;

				rootParameters[0].ParameterType = RootParameterType.Type32BitConstants;
				rootParameters[0].Anonymous.Constants = new()
				{
					ShaderRegister = 0,
					RegisterSpace = 0,
					Num32BitValues = 16 // viewProjection
				};
				rootParameters[0].ShaderVisibility = ShaderVisibility.Vertex;

				rootParameters[1].ParameterType = RootParameterType.TypeDescriptorTable;
				rootParameters[1].Anonymous.DescriptorTable.NumDescriptorRanges = 1;
				rootParameters[1].Anonymous.DescriptorTable.PDescriptorRanges = ranges;
				rootParameters[1].ShaderVisibility = ShaderVisibility.Pixel;

				staticSampler[0] = new()
				{
					Filter = Filter.MinMagMipLinear,
					AddressU = TextureAddressMode.Clamp,
					AddressV = TextureAddressMode.Clamp,
					AddressW = TextureAddressMode.Clamp,
					MipLODBias = 0.0f,
					MaxAnisotropy = 0,
					ComparisonFunc = ComparisonFunc.Always,
					BorderColor = StaticBorderColor.OpaqueWhite,
					MinLOD = 0.0f,
					MaxLOD = float.MaxValue,
					ShaderRegister = 0,
					RegisterSpace = 0,
					ShaderVisibility = ShaderVisibility.Pixel
				};
				break;
			}
			default:
				throw new NotSupportedException($"Unsupported graphics layout '{layout}'.");
		}

		var rootParameterCount = layout == GraphicsLayoutKind.Skybox ? 2 : 4;

		var rootSignatureDesc = new RootSignatureDesc
		{
			NumParameters = (uint)rootParameterCount,
			PParameters = rootParameters,
			NumStaticSamplers = 1,
			PStaticSamplers = staticSampler,
			Flags = RootSignatureFlags.AllowInputAssemblerInputLayout
		};

		var versionedDesc = new VersionedRootSignatureDesc
		{
			Version = D3DRootSignatureVersion.Version10
		};
		versionedDesc.Anonymous.Desc10 = rootSignatureDesc;

		ID3D10Blob* rootSignatureBlob = null;
		ID3D10Blob* rootSignatureError = null;
		var serializeResult =
			_d3d12.SerializeVersionedRootSignature(&versionedDesc, &rootSignatureBlob, &rootSignatureError);
		try
		{
			HandleRootSignatureErrors(serializeResult, rootSignatureError, "graphics");

			SilkMarshal.ThrowHResult(_device.CreateRootSignature(
				0,
				rootSignatureBlob->GetBufferPointer(),
				rootSignatureBlob->GetBufferSize(),
				out ComPtr<ID3D12RootSignature> rootSignature));

			_graphicsRootSignatures[layout] = rootSignature;
			return rootSignature;
		}
		finally
		{
			if (rootSignatureBlob is not null)
			{
				rootSignatureBlob->Release();
			}
		}
	}

	private void EnsureComputeRootSignature()
	{
		if (_computeRootSignature.Handle is not null)
		{
			return;
		}

		var ranges = stackalloc DescriptorRange[2];
		ranges[0].RangeType = DescriptorRangeType.Srv;
		ranges[0].NumDescriptors = 9;
		ranges[0].BaseShaderRegister = 0;
		ranges[0].RegisterSpace = 0;
		ranges[0].OffsetInDescriptorsFromTableStart = 0;

		ranges[1].RangeType = DescriptorRangeType.Uav;
		ranges[1].NumDescriptors = 1;
		ranges[1].BaseShaderRegister = 0;
		ranges[1].RegisterSpace = 0;
		ranges[1].OffsetInDescriptorsFromTableStart = 0xFFFFFFFF;

		var rootParameters = stackalloc RootParameter[3];
		rootParameters[0].ParameterType = RootParameterType.TypeDescriptorTable;
		rootParameters[0].Anonymous.DescriptorTable.NumDescriptorRanges = 2;
		rootParameters[0].Anonymous.DescriptorTable.PDescriptorRanges = ranges;
		rootParameters[0].ShaderVisibility = ShaderVisibility.All;

		rootParameters[1].ParameterType = RootParameterType.Type32BitConstants;
		rootParameters[1].Anonymous.Constants = new()
		{
			ShaderRegister = 0,
			RegisterSpace = 0,
			Num32BitValues = 20 // CameraParams
		};
		rootParameters[1].ShaderVisibility = ShaderVisibility.All;

		rootParameters[2].ParameterType = RootParameterType.Type32BitConstants;
		rootParameters[2].Anonymous.Constants = new()
		{
			ShaderRegister = 1,
			RegisterSpace = 0,
			Num32BitValues = 40 // LightingParams (count + up to 3 lights)
		};
		rootParameters[2].ShaderVisibility = ShaderVisibility.All;

		var staticSampler = stackalloc StaticSamplerDesc[1];
		staticSampler[0] = new()
		{
			Filter = Filter.MinMagMipLinear,
			AddressU = TextureAddressMode.Clamp,
			AddressV = TextureAddressMode.Clamp,
			AddressW = TextureAddressMode.Clamp,
			MipLODBias = 0.0f,
			MaxAnisotropy = 0,
			ComparisonFunc = ComparisonFunc.Always,
			BorderColor = StaticBorderColor.TransparentBlack,
			MinLOD = 0.0f,
			MaxLOD = float.MaxValue,
			ShaderRegister = 0,
			RegisterSpace = 0,
			ShaderVisibility = ShaderVisibility.All
		};

		var rootSignatureDesc = new RootSignatureDesc
		{
			NumParameters = 3,
			PParameters = rootParameters,
			NumStaticSamplers = 1,
			PStaticSamplers = staticSampler,
			Flags = RootSignatureFlags.None
		};

		var versionedDesc = new VersionedRootSignatureDesc
		{
			Version = D3DRootSignatureVersion.Version10
		};
		versionedDesc.Anonymous.Desc10 = rootSignatureDesc;

		ID3D10Blob* rootSignatureBlob = null;
		ID3D10Blob* rootSignatureError = null;
		var serializeResult =
			_d3d12.SerializeVersionedRootSignature(&versionedDesc, &rootSignatureBlob, &rootSignatureError);
		try
		{
			HandleRootSignatureErrors(serializeResult, rootSignatureError, "compute");

			SilkMarshal.ThrowHResult(_device.CreateRootSignature(
				0,
				rootSignatureBlob->GetBufferPointer(),
				rootSignatureBlob->GetBufferSize(),
				out _computeRootSignature));
		}
		finally
		{
			if (rootSignatureBlob is not null)
			{
				rootSignatureBlob->Release();
			}
		}
	}

	private static void HandleRootSignatureErrors(int result, ID3D10Blob* errorBlob, string kind)
	{
		string? errorMessage = null;
		if (errorBlob is not null)
		{
			errorMessage = Marshal.PtrToStringAnsi((nint) errorBlob->GetBufferPointer());
			errorBlob->Release();
		}

		if (result < 0)
		{
			throw new InvalidOperationException($"Failed to serialize {kind} root signature: {errorMessage ?? "Unknown error"}");
		}
	}

	private static void CopySemantic(ReadOnlySpan<byte> source, Span<byte> destination)
	{
		source.CopyTo(destination);
		destination[source.Length] = 0;
	}

	private static RenderStateDescriptor CreateNormalizedRenderState(RenderStateDescriptor state)
	{
		var defaultState = new RenderStateDescriptor(
			AbstractionFillMode.Solid,
			AbstractionCullMode.Back,
			depthTestEnabled: true,
			depthWriteEnabled: true,
			BlendMode.Opaque);

		return state.Equals(default) ? defaultState : state;
	}

	private static BlendDesc CreateBlendState(RenderStateDescriptor state, int targetCount)
	{
		var blendDesc = new BlendDesc
		{
			AlphaToCoverageEnable = 0,
			IndependentBlendEnable = 0
		};

		var count = Math.Max(1, targetCount);
		for (var i = 0; i < count; i++)
		{
			blendDesc.RenderTarget[i] = CreateRenderTargetBlendDesc(state.BlendMode);
		}

		return blendDesc;
	}

	private static RenderTargetBlendDesc CreateRenderTargetBlendDesc(BlendMode blendMode)
	{
		var desc = new RenderTargetBlendDesc
		{
			RenderTargetWriteMask = (byte) ColorWriteEnable.All,
			LogicOp = LogicOp.Noop,
			LogicOpEnable = 0
		};

		switch (blendMode)
		{
			case BlendMode.Additive:
				desc.BlendEnable = 1;
				desc.SrcBlend = Blend.One;
				desc.DestBlend = Blend.One;
				desc.BlendOp = BlendOp.Add;
				desc.SrcBlendAlpha = Blend.One;
				desc.DestBlendAlpha = Blend.One;
				desc.BlendOpAlpha = BlendOp.Add;
				break;
			case BlendMode.AlphaBlend:
				desc.BlendEnable = 1;
				desc.SrcBlend = Blend.SrcAlpha;
				desc.DestBlend = Blend.InvSrcAlpha;
				desc.BlendOp = BlendOp.Add;
				desc.SrcBlendAlpha = Blend.One;
				desc.DestBlendAlpha = Blend.One;
				desc.BlendOpAlpha = BlendOp.Add;
				break;
			default:
				desc.BlendEnable = 0;
				desc.SrcBlend = Blend.One;
				desc.DestBlend = Blend.Zero;
				desc.BlendOp = BlendOp.Add;
				desc.SrcBlendAlpha = Blend.One;
				desc.DestBlendAlpha = Blend.Zero;
				desc.BlendOpAlpha = BlendOp.Add;
				break;
		}

		return desc;
	}

	private static RasterizerDesc CreateRasterizerState(RenderStateDescriptor state)
	{
		return new RasterizerDesc
		{
			FillMode = state.FillMode switch
			{
				AbstractionFillMode.Wireframe => D3DFillMode.Wireframe,
				_ => D3DFillMode.Solid
			},
			CullMode = state.CullMode switch
			{
				AbstractionCullMode.None => D3DCullMode.None,
				AbstractionCullMode.Front => D3DCullMode.Front,
				_ => D3DCullMode.Back
			},
			FrontCounterClockwise = 0,
			DepthBias = D3D12Api.DefaultDepthBias,
			DepthBiasClamp = 0.0f,
			SlopeScaledDepthBias = 0.0f,
			DepthClipEnable = 1,
			MultisampleEnable = 0,
			AntialiasedLineEnable = 0,
			ForcedSampleCount = 0,
			ConservativeRaster = ConservativeRasterizationMode.Off
		};
	}

	private static DepthStencilDesc CreateDepthStencilState(RenderStateDescriptor state)
	{
		return new DepthStencilDesc
		{
			DepthEnable = state.DepthTestEnabled ? (byte) 1 : (byte) 0,
			DepthWriteMask = state.DepthWriteEnabled ? DepthWriteMask.All : DepthWriteMask.Zero,
			DepthFunc = ComparisonFunc.Less,
			StencilEnable = 0,
			StencilReadMask = D3D12Api.DefaultStencilReadMask,
			StencilWriteMask = D3D12Api.DefaultStencilWriteMask,
			FrontFace = new()
			{
				StencilFailOp = StencilOp.Keep,
				StencilDepthFailOp = StencilOp.Keep,
				StencilPassOp = StencilOp.Keep,
				StencilFunc = ComparisonFunc.Always
			},
			BackFace = new()
			{
				StencilFailOp = StencilOp.Keep,
				StencilDepthFailOp = StencilOp.Keep,
				StencilPassOp = StencilOp.Keep,
				StencilFunc = ComparisonFunc.Always
			}
		};
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
		public string? Name => null;

		public TextureDescriptor Descriptor { get; private set; }

		public ID3D12Resource* Resource { get; private set; }

		public CpuDescriptorHandle? RenderTargetView { get; private set; }

		public CpuDescriptorHandle? DepthStencilView { get; private set; }

		public void Initialize(TextureDescriptor descriptor, ID3D12Resource* resource, CpuDescriptorHandle? rtv,
			CpuDescriptorHandle? dsv)
		{
			Descriptor = descriptor;
			Resource = resource;
			RenderTargetView = rtv;
			DepthStencilView = dsv;
		}

		public void Reset()
		{
			Descriptor = default;
			Resource = null;
			RenderTargetView = null;
			DepthStencilView = null;
		}
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

		if ((usage & TextureUsage.UnorderedAccess) != 0)
		{
			flags |= ResourceFlags.AllowUnorderedAccess;
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

		if ((usage & TextureUsage.UnorderedAccess) != 0)
		{
			return ResourceStates.UnorderedAccess;
		}

		return ResourceStates.Common;
	}

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern nint CreateEventEx(nint lpEventAttributes, string? lpName, uint dwFlags, uint dwDesiredAccess);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);
}
