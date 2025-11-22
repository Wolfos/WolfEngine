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
	private ComPtr<ID3D12RootSignature> _graphicsRootSignature;
	private ComPtr<ID3D12RootSignature> _computeRootSignature;
	private readonly Dictionary<CommandListType, Queue<D3D12CommandList>> _commandListPool = new();
	private readonly Queue<D3D12Texture> _texturePool = new();
	private readonly object _texturePoolLock = new();
	
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

		var texture = RentTextureWrapper();
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

		return new ExternalD3D12Texture(descriptor, resource, rtvHandle, dsvHandle);
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

	private D3D12Texture RentTextureWrapper()
	{
		lock (_texturePoolLock)
		{
			if (_texturePool.Count > 0)
			{
				return _texturePool.Dequeue();
			}
		}

		return new D3D12Texture();
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

	public bool ReturnTexture(IGfxTexture texture)
	{
		if (texture is not D3D12Texture d3dTexture)
		{
			return false;
		}

		d3dTexture.Dispose();

		lock (_texturePoolLock)
		{
			_texturePool.Enqueue(d3dTexture);
		}

		return true;
	}

	public void ClearTexturePool()
	{
		lock (_texturePoolLock)
		{
			while (_texturePool.Count > 0)
			{
				_texturePool.Dequeue().Dispose();
			}
		}
	}

	private D3D12Pipeline CreateGraphicsPipeline(PipelineKey key, in ShaderBytecodeSet shaders)
	{
		if (shaders.Vertex.HasValue == false || shaders.Pixel.HasValue == false)
		{
			throw new InvalidOperationException("Graphics pipelines require both vertex and pixel shaders.");
		}

		EnsureGraphicsRootSignature();

		var vertexShader = shaders.Vertex.Value.Span;
		var pixelShader = shaders.Pixel.Value.Span;

		Span<byte> positionSemantic = stackalloc byte["POSITION".Length + 1];
		Span<byte> normalSemantic = stackalloc byte["NORMAL".Length + 1];
		CopySemantic("POSITION"u8, positionSemantic);
		CopySemantic("NORMAL"u8, normalSemantic);

		var inputElements = stackalloc InputElementDesc[2];
		fixed (byte* positionPtr = positionSemantic)
		fixed (byte* normalPtr = normalSemantic)
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

			var inputLayout = new InputLayoutDesc
			{
				PInputElementDescs = inputElements,
				NumElements = 2
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
					PRootSignature = _graphicsRootSignature.Handle,
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

				return new D3D12Pipeline(key, PassKind.Graphics, pipelineState, _graphicsRootSignature);
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

	private void EnsureGraphicsRootSignature()
	{
		if (_graphicsRootSignature.Handle is not null)
		{
			return;
		}

		var rootParameters = stackalloc RootParameter[3];
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

		var rootSignatureDesc = new RootSignatureDesc
		{
			NumParameters = 3,
			PParameters = rootParameters,
			NumStaticSamplers = 0,
			PStaticSamplers = null,
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
				out _graphicsRootSignature));
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

		var srvRange = stackalloc DescriptorRange[1];
		srvRange[0].RangeType = DescriptorRangeType.Srv;
		srvRange[0].NumDescriptors = 4;
		srvRange[0].BaseShaderRegister = 0;
		srvRange[0].RegisterSpace = 0;
		srvRange[0].OffsetInDescriptorsFromTableStart = 0;

		var uavRange = stackalloc DescriptorRange[1];
		uavRange[0].RangeType = DescriptorRangeType.Uav;
		uavRange[0].NumDescriptors = 1;
		uavRange[0].BaseShaderRegister = 0;
		uavRange[0].RegisterSpace = 0;
		uavRange[0].OffsetInDescriptorsFromTableStart = 0;

		var rootParameters = stackalloc RootParameter[3];
		rootParameters[0].ParameterType = RootParameterType.TypeDescriptorTable;
		rootParameters[0].Anonymous.DescriptorTable.NumDescriptorRanges = 1;
		rootParameters[0].Anonymous.DescriptorTable.PDescriptorRanges = srvRange;
		rootParameters[0].ShaderVisibility = ShaderVisibility.All;

		rootParameters[1].ParameterType = RootParameterType.TypeDescriptorTable;
		rootParameters[1].Anonymous.DescriptorTable.NumDescriptorRanges = 1;
		rootParameters[1].Anonymous.DescriptorTable.PDescriptorRanges = uavRange;
		rootParameters[1].ShaderVisibility = ShaderVisibility.All;

		rootParameters[2].ParameterType = RootParameterType.Type32BitConstants;
		rootParameters[2].Anonymous.Constants = new()
		{
			ShaderRegister = 0,
			RegisterSpace = 0,
			Num32BitValues = 20
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
}
