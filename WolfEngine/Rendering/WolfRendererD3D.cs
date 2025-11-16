using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using WolfEngine.ECS;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Backend.D3D12;
using WolfEngine.Rendering.Passes;
using D3DVertexBufferView = Silk.NET.Direct3D12.VertexBufferView;
using D3DIndexBufferView = Silk.NET.Direct3D12.IndexBufferView;
using D3DFillMode = Silk.NET.Direct3D12.FillMode;
using D3DCullMode = Silk.NET.Direct3D12.CullMode;
using Range = Silk.NET.Direct3D12.Range;

namespace WolfEngine;

public unsafe class WolfRendererD3D : IRenderer
{
	private const int FrameCount = 2;

	private readonly float[] _backgroundColour = [0.392f, 0.584f, 0.929f, 1.0f];

	private sealed class MeshResources
	{
		public MeshResources(
			ComPtr<ID3D12Resource> vertexBuffer,
			ComPtr<ID3D12Resource> indexBuffer,
			D3DVertexBufferView vertexView,
			D3DIndexBufferView indexView,
			uint indexCount)
		{
			VertexBuffer = vertexBuffer;
			IndexBuffer = indexBuffer;
			VertexView = vertexView;
			IndexView = indexView;
			IndexCount = indexCount;
		}

		public ComPtr<ID3D12Resource> VertexBuffer { get; }

		public ComPtr<ID3D12Resource> IndexBuffer { get; }

		public D3DVertexBufferView VertexView { get; }

		public D3DIndexBufferView IndexView { get; }

		public uint IndexCount { get; }
	}

	private struct VertexData
	{
		public Vector4 Position;
		public Vector3 Normal;
		public float Padding;
	}

	private readonly struct DrawInstruction
	{
		public DrawInstruction(Mesh mesh, Material material, Matrix4x4 transform)
		{
			Mesh = mesh;
			Material = material;
			Transform = transform;
		}

		public Mesh Mesh { get; }

		public Material Material { get; }

		public Matrix4x4 Transform { get; }
	}

	private readonly int _width;
	private readonly int _height;
	private readonly IShaderCompiler _shaderCompiler;
	private readonly IArenaAllocator _arenaAllocator;
	private IWindow _window = null!;
	private IInputContext _inputContext = null!;
	private Action _startupCallback = static () => { };
	private Action<float> _updateCallback = static deltaTime => { };
	private Action<float> _renderCallback = static deltaTime => { };
	private bool _isInitialized;

	private DXGI _dxgi = null!;
	private D3D12 _d3d12 = null!;
	private ComPtr<IDXGIFactory2> _factory;
	private ComPtr<IDXGISwapChain3> _swapchain;
	private ComPtr<ID3D12Device> _device;
	private ComPtr<IDXGIAdapter> _adapter = default;
	private ComPtr<ID3D12CommandQueue> _commandQueue;
	private D3D12Device _gfxDevice = null!;

	private ComPtr<ID3D12DescriptorHeap> _rtvHeap;
	private uint _rtvDescriptorSize;
	private readonly CpuDescriptorHandle[] _rtvCpuHandles = new CpuDescriptorHandle[FrameCount];
	private readonly ulong[] _frameFenceValues = new ulong[FrameCount];
	private readonly ComPtr<ID3D12Resource>[] _renderTargets = new ComPtr<ID3D12Resource>[FrameCount];

	private readonly ComPtr<ID3D12CommandAllocator>[] _commandAllocators =
		new ComPtr<ID3D12CommandAllocator>[FrameCount];

	private ComPtr<ID3D12GraphicsCommandList> _commandList;
	private ID3D12GraphicsCommandList* _activeCommandList;
	private IGfxCommandList _currentGfxCommandList = null!;
	private ComPtr<ID3D12Fence> _fence;
	private ulong _fenceValue;
	private nint _fenceEvent = nint.Zero;
	private ComPtr<ID3D12RootSignature> _rootSignature;
	private ComPtr<ID3D12PipelineState> _gbufferPipeline;
	private ComPtr<ID3D12RootSignature> _lightingRootSignature;
	private ComPtr<ID3D12PipelineState> _lightingPipeline;
	private ComPtr<ID3D12DescriptorHeap> _dsvHeap;
	private ComPtr<ID3D12Resource> _depthBuffer;
	private ComPtr<ID3D12DescriptorHeap> _lightingDescriptorHeap;
	private uint _lightingDescriptorSize;
	private ComPtr<ID3D12Resource> _lightingBuffer;
	private readonly ConcurrentQueue<RenderCommand> _pendingCommands = new();
	private readonly Dictionary<Mesh, MeshResources> _meshResources = new();
	private readonly List<DrawInstruction> _drawCommands = new();
	private Camera _camera;
	private Transform _cameraTransform;
	private bool _hasCamera;

	private uint _backbufferIndex;
	private nint _windowHandle;
	private Int2 _framebufferSize = Int2.Zero;

	public WolfRendererD3D(IShaderCompiler shaderCompiler, IArenaAllocator arenaAllocator)
	{
		_width = 1280;
		_height = 720;
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_arenaAllocator = arenaAllocator ?? throw new ArgumentNullException(nameof(arenaAllocator));
	}

	public void Run(Action startup, Action<float> update, Action<float> render)
	{
		_startupCallback = startup;
		_updateCallback = update;
		_renderCallback = render;

		InitializeWindow();

		var window = _window ?? throw new InvalidOperationException("Window was not initialised.");

		window.Load += OnWindowLoad;
		window.Update += OnWindowUpdate;
		window.Render += OnWindowRender;
		window.FramebufferResize += OnWindowFramebufferResize;
		window.Closing += OnWindowClosing;

		try
		{
			window.Run();
		}
		finally
		{
			Dispose();
		}
	}

	public void SubmitCommand(RenderCommand command)
	{
		_pendingCommands.Enqueue(command);
	}

	private void OnWindowLoad()
	{
		RetrieveNativeHandle();
		UpdateFramebufferSize();

		OnLoad();

		if (_window is null)
		{
			throw new InvalidOperationException("Window was not initialised.");
		}

		_inputContext = _window.CreateInput();
		foreach (var keyboard in _inputContext.Keyboards)
		{
			keyboard.KeyDown += HandleKeyDown;
		}

		_startupCallback();
		ProcessPendingCommands();
		_isInitialized = true;
	}

	private void OnWindowUpdate(double deltaTime)
	{
		_updateCallback((float)deltaTime);
		OnUpdate((float)deltaTime);
	}

	private void OnWindowRender(double deltaTime)
	{
		if (_isInitialized == false)
		{
			return;
		}

		if (_framebufferSize.X <= 0 || _framebufferSize.Y <= 0)
		{
			return;
		}

		_renderCallback((float)deltaTime);
	}

	private void OnWindowFramebufferResize(Vector2D<int> newSize)
	{
		if (_isInitialized == false)
		{
			if (newSize.X > 0 && newSize.Y > 0)
			{
				_framebufferSize = new Int2(newSize.X, newSize.Y);
			}

			return;
		}

		OnFramebufferResize(new Int2(newSize.X, newSize.Y));
	}

	private void OnWindowClosing()
	{
		_isInitialized = false;
	}

	private void HandleKeyDown(IKeyboard keyboard, Key key, int keycode)
	{
		if (key == Key.Escape)
		{
			_window?.Close();
		}
	}

	private void InitializeWindow()
	{
		var options = WindowOptions.Default;
		options.Title = "WolfEngine";
		options.Size = new Vector2D<int>(_width, _height);
		options.API = GraphicsAPI.None;

		_window = Window.Create(options);
	}

	private void RetrieveNativeHandle()
	{
		if (_window is null)
		{
			throw new InvalidOperationException("Window was not initialised.");
		}

		var win32 = _window.Native.Win32;
		if (win32 is null)
		{
			throw new InvalidOperationException("Direct3D renderer requires a Win32 window handle.");
		}

		_windowHandle = win32.Value.Hwnd;
		if (_windowHandle == nint.Zero)
		{
			throw new InvalidOperationException("Windowing subsystem reported a null window handle.");
		}
	}

	private void UpdateFramebufferSize()
	{
		if (_window is null)
		{
			return;
		}

		var size = _window.FramebufferSize;
		if (size.X > 0 && size.Y > 0)
		{
			_framebufferSize = new Int2(size.X, size.Y);
		}
	}

	private void OnLoad()
	{
#pragma warning disable CS0618
		_dxgi = DXGI.GetApi();
#pragma warning restore CS0618
		_d3d12 = D3D12.GetApi();

		CreateDeviceAndQueue();
		CreateSwapchain();
		CreateRtvHeapAndTargets();
		CreateCommandAllocatorsAndList();
		CreateSyncObjects();
		CreateRootSignature();
		CreateDepthResources();
		CreateGBufferPipeline();
		CreateLightingDescriptors();
		CreateLightingBuffer();
		CreateLightingPipeline();
	}

	private void CreateDeviceAndQueue()
	{
		SilkMarshal.ThrowHResult(
			_d3d12.CreateDevice(
				_adapter,
				D3DFeatureLevel.Level120,
				out _device));
		var commandQueueDescription = new CommandQueueDesc(
			type: CommandListType.Direct,
			priority: (int) CommandQueuePriority.Normal,
			flags: CommandQueueFlags.None);

		SilkMarshal.ThrowHResult(_device.CreateCommandQueue(in commandQueueDescription, out _commandQueue));

		_gfxDevice = new D3D12Device(_device, _commandQueue);
	}

	public IMaterialResources CreateMaterialResources(Material material)
	{
		if (material is null)
		{
			throw new ArgumentNullException(nameof(material));
		}

		var vertexShaderBytes = _shaderCompiler.GetDxil(material.ShaderPath, "vertexShader", "vs_6_0");
		var pixelShaderBytes = _shaderCompiler.GetDxil(material.ShaderPath, "fragmentShader", "ps_6_0");

		InputLayoutDesc inputLayout;

		var inputElements = stackalloc InputElementDesc[2];
		Span<byte> positionSemantic =
			[(byte) 'P', (byte) 'O', (byte) 'S', (byte) 'I', (byte) 'T', (byte) 'I', (byte) 'O', (byte) 'N', 0];
		inputElements[0] = new InputElementDesc
		{
			SemanticName = (byte*) Unsafe.AsPointer(ref positionSemantic.GetPinnableReference()),
			SemanticIndex = 0,
			Format = Format.FormatR32G32B32A32Float,
			InputSlot = 0,
			AlignedByteOffset = 0,
			InputSlotClass = InputClassification.PerVertexData,
			InstanceDataStepRate = 0
		};

		Span<byte> normalSemantic = [(byte) 'N', (byte) 'O', (byte) 'R', (byte) 'M', (byte) 'A', (byte) 'L', 0];
		inputElements[1] = new InputElementDesc
		{
			SemanticName = (byte*) Unsafe.AsPointer(ref normalSemantic.GetPinnableReference()),
			SemanticIndex = 0,
			Format = Format.FormatR32G32B32Float,
			InputSlot = 0,
			AlignedByteOffset = 16,
			InputSlotClass = InputClassification.PerVertexData,
			InstanceDataStepRate = 0
		};

		inputLayout = new InputLayoutDesc
		{
			PInputElementDescs = inputElements,
			NumElements = 2
		};


		var blendState = new BlendDesc
		{
			AlphaToCoverageEnable = 0,
			IndependentBlendEnable = 0
		};
		blendState.RenderTarget[0] = new RenderTargetBlendDesc
		{
			BlendEnable = 0,
			LogicOpEnable = 0,
			SrcBlend = Blend.One,
			DestBlend = Blend.Zero,
			BlendOp = BlendOp.Add,
			SrcBlendAlpha = Blend.One,
			DestBlendAlpha = Blend.Zero,
			BlendOpAlpha = BlendOp.Add,
			LogicOp = LogicOp.Noop,
			RenderTargetWriteMask = (byte) ColorWriteEnable.All
		};

		var rasterizerState = new RasterizerDesc
		{
			FillMode = D3DFillMode.Solid,
			CullMode = D3DCullMode.Back,
			FrontCounterClockwise = 0,
			DepthBias = D3D12.DefaultDepthBias,
			DepthBiasClamp = 0.0f,
			SlopeScaledDepthBias = 0.0f,
			DepthClipEnable = 1,
			MultisampleEnable = 0,
			AntialiasedLineEnable = 0,
			ForcedSampleCount = 0,
			ConservativeRaster = ConservativeRasterizationMode.Off
		};

		var depthStencilState = new DepthStencilDesc
		{
			DepthEnable = 1,
			DepthWriteMask = DepthWriteMask.All,
			DepthFunc = ComparisonFunc.Less,
			StencilEnable = 0,
			StencilReadMask = D3D12.DefaultStencilReadMask,
			StencilWriteMask = D3D12.DefaultStencilWriteMask,
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

		ComPtr<ID3D12PipelineState> pipelineState = default;

		fixed (byte* vertexPtr = vertexShaderBytes)
		fixed (byte* pixelPtr = pixelShaderBytes)
		{
			var shaderBytecodeVS = new ShaderBytecode
			{
				PShaderBytecode = vertexPtr,
				BytecodeLength = (nuint) vertexShaderBytes.Length
			};

			var shaderBytecodePS = new ShaderBytecode
			{
				PShaderBytecode = pixelPtr,
				BytecodeLength = (nuint) pixelShaderBytes.Length
			};

			var psoDesc = new GraphicsPipelineStateDesc
			{
				PRootSignature = _rootSignature.Handle,
				VS = shaderBytecodeVS,
				PS = shaderBytecodePS,
				BlendState = blendState,
				SampleMask = D3D12.DefaultSampleMask,
				RasterizerState = rasterizerState,
				DepthStencilState = depthStencilState,
				InputLayout = inputLayout,
				IBStripCutValue = IndexBufferStripCutValue.ValueDisabled,
				PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
				NumRenderTargets = 1,
				DSVFormat = Format.FormatUnknown,
				SampleDesc = new SampleDesc(1, 0),
				NodeMask = 0,
				CachedPSO = default,
				Flags = PipelineStateFlags.None
			};
			psoDesc.RTVFormats[0] = Format.FormatB8G8R8A8Unorm;

			SilkMarshal.ThrowHResult(_device.CreateGraphicsPipelineState(in psoDesc, out pipelineState));
		}

		var colorSize = Align((ulong) Unsafe.SizeOf<Vector4>(),
			D3D12.ConstantBufferDataPlacementAlignment);
		var uploadProps = new HeapProperties(HeapType.Upload);
		var bufferDesc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = colorSize,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = Format.FormatUnknown,
			SampleDesc = new SampleDesc(1, 0),
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.None
		};

		ComPtr<ID3D12Resource> colorBuffer;
		SilkMarshal.ThrowHResult(
			_device.CreateCommittedResource(
				&uploadProps,
				HeapFlags.None,
				in bufferDesc,
				ResourceStates.GenericRead,
				null,
				out colorBuffer));

		void* mappedData = null;
		SilkMarshal.ThrowHResult(colorBuffer.Map(0, (Range*) null, &mappedData));
		try
		{
			var color = material.Color;
			Unsafe.Write((Vector4*) mappedData, color);
		}
		finally
		{
			colorBuffer.Unmap(0, (Range*) null);
		}

		return new D3D12MaterialResources
		{
			ColorBuffer = colorBuffer,
			PipelineState = pipelineState
		};
	}

	public IGfxDevice GetGfxDevice()
	{
		return _gfxDevice;
	}

	public Int2 GetFrameBufferSize()
	{
		return _framebufferSize;
	}

	private void CreateSwapchain()
	{
		var swapChainDesc = new SwapChainDesc1
		{
			BufferCount = FrameCount,
			Format = Format.FormatB8G8R8A8Unorm,
			BufferUsage = DXGI.UsageRenderTargetOutput,
			SwapEffect = SwapEffect.FlipDiscard,
			SampleDesc = new(1, 0),
			Width = (uint) Math.Max(_framebufferSize.X, 1),
			Height = (uint) Math.Max(_framebufferSize.Y, 1)
		};

		_factory = _dxgi.CreateDXGIFactory<IDXGIFactory2>();

		var factoryPtr = _factory.Handle;
		var queuePtr = (IUnknown*) _commandQueue.Handle;
		IDXGISwapChain1* swapChain1 = null;
		var swapchainResult = factoryPtr->CreateSwapChainForHwnd(
			queuePtr,
			_windowHandle,
			&swapChainDesc,
			null,
			null,
			&swapChain1);
		SilkMarshal.ThrowHResult(swapchainResult);

		IDXGISwapChain3* swapChain3 = null;
		var swapChain3Guid = IDXGISwapChain3.Guid;
		SilkMarshal.ThrowHResult(swapChain1->QueryInterface(ref swapChain3Guid, (void**) &swapChain3));
		_swapchain = new ComPtr<IDXGISwapChain3>(swapChain3);

		swapChain1->Release();

		_backbufferIndex = _swapchain.GetCurrentBackBufferIndex();
	}

	private void CreateRtvHeapAndTargets()
	{
		var rtvHeapDesc = new DescriptorHeapDesc
		{
			Type = DescriptorHeapType.Rtv,
			NumDescriptors = FrameCount,
			Flags = DescriptorHeapFlags.None,
			NodeMask = 0
		};

		SilkMarshal.ThrowHResult(_device.CreateDescriptorHeap(in rtvHeapDesc, out _rtvHeap));
		_rtvDescriptorSize = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Rtv);

		var rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
		for (var i = 0; i < FrameCount; i++)
		{
			_rtvCpuHandles[i] = rtvHandle;
			SilkMarshal.ThrowHResult(_swapchain.GetBuffer((uint) i, out _renderTargets[i]));
			_device.CreateRenderTargetView(_renderTargets[i], null, rtvHandle);
			rtvHandle.Ptr += _rtvDescriptorSize;
		}
	}

	private void CreateCommandAllocatorsAndList()
	{
		for (var i = 0; i < FrameCount; i++)
		{
			SilkMarshal.ThrowHResult(_device.CreateCommandAllocator(CommandListType.Direct, out _commandAllocators[i]));
		}

		SilkMarshal.ThrowHResult(
			_device.CreateCommandList<ID3D12CommandAllocator, ID3D12PipelineState, ID3D12GraphicsCommandList>(
				0,
				CommandListType.Direct,
				_commandAllocators[0],
				default,
				out _commandList));

		SilkMarshal.ThrowHResult(_commandList.Close());
	}

	private void CreateSyncObjects()
	{
		SilkMarshal.ThrowHResult(_device.CreateFence(0, FenceFlags.None, out _fence));
		_fenceValue = 0;
		_fenceEvent = CreateEventEx(nint.Zero, null, 0, 0x1F0003);
		if (_fenceEvent == nint.Zero)
		{
			throw new InvalidOperationException("Failed to create fence event.");
		}
	}

	private void CreateRootSignature()
	{
		var rootParameters = stackalloc RootParameter[3];
		rootParameters[0].ParameterType = RootParameterType.TypeCbv;
		rootParameters[0].Anonymous.Descriptor = new RootDescriptor
		{
			ShaderRegister = 0,
			RegisterSpace = 0
		};
		rootParameters[0].ShaderVisibility = ShaderVisibility.Pixel;

		rootParameters[1].ParameterType = RootParameterType.Type32BitConstants;
		rootParameters[1].Anonymous.Constants = new RootConstants
		{
			ShaderRegister = 1,
			RegisterSpace = 0,
			Num32BitValues = 16
		};
		rootParameters[1].ShaderVisibility = ShaderVisibility.Vertex;

		rootParameters[2].ParameterType = RootParameterType.Type32BitConstants;
		rootParameters[2].Anonymous.Constants = new RootConstants
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
		if (rootSignatureError is not null)
		{
			var message = Marshal.PtrToStringAnsi((nint) rootSignatureError->GetBufferPointer());
			rootSignatureError->Release();
			if (serializeResult < 0)
			{
				throw new InvalidOperationException($"Failed to serialise root signature: {message}");
			}
		}

		SilkMarshal.ThrowHResult(serializeResult);

		SilkMarshal.ThrowHResult(_device.CreateRootSignature(
			0,
			rootSignatureBlob->GetBufferPointer(),
			rootSignatureBlob->GetBufferSize(),
			out _rootSignature));
		rootSignatureBlob->Release();
	}

	private void CreateGBufferPipeline()
	{
		var vertexShaderBytes = _shaderCompiler.GetDxil("gbuffer.slang", "vertexShader", "vs_6_6");
		var pixelShaderBytes = _shaderCompiler.GetDxil("gbuffer.slang", "fragmentShader", "ps_6_6");

		ReadOnlySpan<byte> positionSemantic = "POSITION"u8;
		ReadOnlySpan<byte> normalSemantic = "NORMAL"u8;

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
		}

		var inputLayout = new InputLayoutDesc
		{
			PInputElementDescs = inputElements,
			NumElements = 2
		};

		var blendState = new BlendDesc
		{
			AlphaToCoverageEnable = 0,
			IndependentBlendEnable = 0
		};
		var blendDesc = new RenderTargetBlendDesc
		{
			BlendEnable = 0,
			LogicOpEnable = 0,
			SrcBlend = Blend.One,
			DestBlend = Blend.Zero,
			BlendOp = BlendOp.Add,
			SrcBlendAlpha = Blend.One,
			DestBlendAlpha = Blend.Zero,
			BlendOpAlpha = BlendOp.Add,
			LogicOp = LogicOp.Noop,
			RenderTargetWriteMask = (byte) ColorWriteEnable.All
		};
		blendState.RenderTarget[0] = blendDesc;
		blendState.RenderTarget[1] = blendDesc;
		blendState.RenderTarget[2] = blendDesc;

		var rasterizerState = new RasterizerDesc
		{
			FillMode = D3DFillMode.Solid,
			CullMode = D3DCullMode.Back,
			FrontCounterClockwise = 0,
			DepthBias = D3D12.DefaultDepthBias,
			DepthBiasClamp = 0.0f,
			SlopeScaledDepthBias = 0.0f,
			DepthClipEnable = 1,
			MultisampleEnable = 0,
			AntialiasedLineEnable = 0,
			ForcedSampleCount = 0,
			ConservativeRaster = ConservativeRasterizationMode.Off
		};

		var depthStencilState = new DepthStencilDesc
		{
			DepthEnable = 1,
			DepthWriteMask = DepthWriteMask.All,
			DepthFunc = ComparisonFunc.Less,
			StencilEnable = 0,
			StencilReadMask = D3D12.DefaultStencilReadMask,
			StencilWriteMask = D3D12.DefaultStencilWriteMask,
			FrontFace = new DepthStencilopDesc
			{
				StencilFailOp = StencilOp.Keep,
				StencilDepthFailOp = StencilOp.Keep,
				StencilPassOp = StencilOp.Keep,
				StencilFunc = ComparisonFunc.Always
			},
			BackFace = new DepthStencilopDesc
			{
				StencilFailOp = StencilOp.Keep,
				StencilDepthFailOp = StencilOp.Keep,
				StencilPassOp = StencilOp.Keep,
				StencilFunc = ComparisonFunc.Always
			}
		};

		fixed (byte* vertexPtr = vertexShaderBytes)
		fixed (byte* pixelPtr = pixelShaderBytes)
		{
			var shaderBytecodeVS = new ShaderBytecode
			{
				PShaderBytecode = vertexPtr,
				BytecodeLength = (nuint) vertexShaderBytes.Length
			};

			var shaderBytecodePS = new ShaderBytecode
			{
				PShaderBytecode = pixelPtr,
				BytecodeLength = (nuint) pixelShaderBytes.Length
			};

			var psoDesc = new GraphicsPipelineStateDesc
			{
				PRootSignature = _rootSignature.Handle,
				VS = shaderBytecodeVS,
				PS = shaderBytecodePS,
				BlendState = blendState,
				SampleMask = D3D12.DefaultSampleMask,
				RasterizerState = rasterizerState,
				DepthStencilState = depthStencilState,
				InputLayout = inputLayout,
				IBStripCutValue = IndexBufferStripCutValue.ValueDisabled,
				PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
				NumRenderTargets = 3,
				DSVFormat = Format.FormatD32Float,
				SampleDesc = new SampleDesc(1, 0),
				NodeMask = 0,
				CachedPSO = default,
				Flags = PipelineStateFlags.None
			};
			psoDesc.RTVFormats[0] = Format.FormatB8G8R8A8Unorm;
			psoDesc.RTVFormats[1] = Format.FormatR16G16B16A16Float;
			psoDesc.RTVFormats[2] = Format.FormatR8G8B8A8Unorm;

			SilkMarshal.ThrowHResult(_device.CreateGraphicsPipelineState(in psoDesc, out _gbufferPipeline));
		}
	}

	private void CreateLightingDescriptors()
	{
		var heapDesc = new DescriptorHeapDesc
		{
			Type = DescriptorHeapType.CbvSrvUav,
			NumDescriptors = 4,
			Flags = DescriptorHeapFlags.ShaderVisible,
			NodeMask = 0
		};

		SilkMarshal.ThrowHResult(_device.CreateDescriptorHeap(in heapDesc, out _lightingDescriptorHeap));
		_lightingDescriptorSize = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.CbvSrvUav);
	}

	private void CreateLightingBuffer()
	{
		if (_lightingBuffer.Handle is not null)
		{
			_lightingBuffer.Dispose();
			_lightingBuffer = default;
		}

		var width = Math.Max(_framebufferSize.X, 1);
		var height = Math.Max(_framebufferSize.Y, 1);

		var desc = new ResourceDesc
		{
			Dimension = ResourceDimension.Texture2D,
			Alignment = 0,
			Width = (ulong) width,
			Height = (uint) height,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = Format.FormatB8G8R8A8Unorm,
			SampleDesc = new SampleDesc(1, 0),
			Layout = TextureLayout.LayoutUnknown,
			Flags = ResourceFlags.AllowUnorderedAccess
		};

		var heapProps = new HeapProperties(HeapType.Default);
		SilkMarshal.ThrowHResult(_device.CreateCommittedResource(
			&heapProps,
			HeapFlags.None,
			in desc,
			ResourceStates.UnorderedAccess,
			null,
			out _lightingBuffer));
	}

	private void CreateLightingPipeline()
	{
		var shaderBytes = _shaderCompiler.GetDxil("deferred_lighting.compute.slang", "CSMain", "cs_6_6");

		var srvRange = stackalloc DescriptorRange[1];
		srvRange[0].RangeType = DescriptorRangeType.Srv;
		srvRange[0].NumDescriptors = 3;
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
		rootParameters[0].ParameterType = 0;
		rootParameters[0].Anonymous.DescriptorTable.NumDescriptorRanges = 1;
		rootParameters[0].Anonymous.DescriptorTable.PDescriptorRanges = srvRange;
		rootParameters[0].ShaderVisibility = ShaderVisibility.All;

		rootParameters[1].ParameterType = 0;
		rootParameters[1].Anonymous.DescriptorTable.NumDescriptorRanges = 1;
		rootParameters[1].Anonymous.DescriptorTable.PDescriptorRanges = uavRange;
		rootParameters[1].ShaderVisibility = ShaderVisibility.All;

		rootParameters[2].ParameterType = RootParameterType.Type32BitConstants;
		rootParameters[2].Anonymous.Constants = new RootConstants
		{
			ShaderRegister = 0,
			RegisterSpace = 0,
			Num32BitValues = 20
		};
		rootParameters[2].ShaderVisibility = ShaderVisibility.All;

		var staticSampler = stackalloc StaticSamplerDesc[1];
		staticSampler[0] = new StaticSamplerDesc
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
		if (rootSignatureError is not null)
		{
			var message = Marshal.PtrToStringAnsi((nint) rootSignatureError->GetBufferPointer());
			rootSignatureError->Release();
			if (serializeResult < 0)
			{
				throw new InvalidOperationException($"Failed to serialise lighting root signature: {message}");
			}
		}

		SilkMarshal.ThrowHResult(serializeResult);
		SilkMarshal.ThrowHResult(_device.CreateRootSignature(
			0,
			rootSignatureBlob->GetBufferPointer(),
			rootSignatureBlob->GetBufferSize(),
			out _lightingRootSignature));
		rootSignatureBlob->Release();

		fixed (byte* shaderPtr = shaderBytes)
		{
			var shaderBytecode = new ShaderBytecode
			{
				PShaderBytecode = shaderPtr,
				BytecodeLength = (nuint) shaderBytes.Length
			};

			var pipelineDesc = new ComputePipelineStateDesc
			{
				PRootSignature = _lightingRootSignature.Handle,
				CS = shaderBytecode,
				NodeMask = 0,
				CachedPSO = default,
				Flags = PipelineStateFlags.None
			};

			SilkMarshal.ThrowHResult(_device.CreateComputePipelineState(in pipelineDesc, out _lightingPipeline));
		}
	}

	private void CreateDepthResources()
	{
		if (_framebufferSize.X <= 0 || _framebufferSize.Y <= 0)
		{
			_framebufferSize = new Int2(_width, _height);
		}

		if (_dsvHeap.Handle is not null)
		{
			_dsvHeap.Dispose();
		}

		var dsvDesc = new DescriptorHeapDesc
		{
			Type = DescriptorHeapType.Dsv,
			NumDescriptors = 1,
			Flags = DescriptorHeapFlags.None,
			NodeMask = 0
		};
		SilkMarshal.ThrowHResult(_device.CreateDescriptorHeap(in dsvDesc, out _dsvHeap));

		if (_depthBuffer.Handle is not null)
		{
			_depthBuffer.Dispose();
		}

		var depthDesc = new ResourceDesc
		{
			Dimension = ResourceDimension.Texture2D,
			Alignment = 0,
			Width = (ulong) Math.Max(_framebufferSize.X, 1),
			Height = (uint) Math.Max(_framebufferSize.Y, 1),
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = Format.FormatD32Float,
			SampleDesc = new SampleDesc(1, 0),
			Layout = TextureLayout.LayoutUnknown,
			Flags = ResourceFlags.AllowDepthStencil
		};

		var depthClearValue = new ClearValue
		{
			Format = Format.FormatD32Float
		};
		depthClearValue.Anonymous.DepthStencil = new DepthStencilValue
		{
			Depth = 1.0f,
			Stencil = 0
		};

		var heapProps = new HeapProperties(HeapType.Default);
		SilkMarshal.ThrowHResult(
			_device.CreateCommittedResource(
				&heapProps,
				HeapFlags.None,
				in depthDesc,
				ResourceStates.DepthWrite,
				&depthClearValue,
				out _depthBuffer));

		var depthHandle = _dsvHeap.GetCPUDescriptorHandleForHeapStart();
		var dsv = new DepthStencilViewDesc
		{
			Format = Format.FormatD32Float,
			ViewDimension = DsvDimension.Texture2D,
			Flags = 0
		};
		_device.CreateDepthStencilView(_depthBuffer, &dsv, depthHandle);
	}

	private void ProcessPendingCommands()
	{
		while (_pendingCommands.TryDequeue(out var command))
		{
			switch (command.Type)
			{
				case RenderCommandType.CreateMesh:
					HandleCreateMeshCommand(command);
					break;
				case RenderCommandType.DrawMesh:
					HandleDrawMeshCommand(command);
					break;
				case RenderCommandType.SetCamera:
					HandleSetCameraCommand(command);
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(command.Type), command.Type,
						"Unsupported render command type.");
			}
		}
	}

	private static void WriteMatrix(Span<float> destination, Matrix4x4 matrix)
	{
		if (destination.Length < 16)
		{
			throw new ArgumentException("Destination span must contain at least 16 elements.", nameof(destination));
		}

		destination[0] = matrix.M11;
		destination[1] = matrix.M12;
		destination[2] = matrix.M13;
		destination[3] = matrix.M14;
		destination[4] = matrix.M21;
		destination[5] = matrix.M22;
		destination[6] = matrix.M23;
		destination[7] = matrix.M24;
		destination[8] = matrix.M31;
		destination[9] = matrix.M32;
		destination[10] = matrix.M33;
		destination[11] = matrix.M34;
		destination[12] = matrix.M41;
		destination[13] = matrix.M42;
		destination[14] = matrix.M43;
		destination[15] = matrix.M44;
	}

	private MeshResources CreateMeshResources(Mesh mesh)
	{
		var vertexCount = mesh.Vertices.Length;
		if (vertexCount == 0)
		{
			throw new InvalidOperationException("Mesh must contain vertex data.");
		}

		var vertices = new VertexData[vertexCount];
		for (var i = 0; i < vertexCount; i++)
		{
			vertices[i].Position = mesh.Vertices[i];
			vertices[i].Normal = i < mesh.Normals.Length ? mesh.Normals[i] : Vector3.UnitY;
			vertices[i].Padding = 0.0f;
		}

		var vertexStride = (uint) Unsafe.SizeOf<VertexData>();
		var vertexBufferSize = (ulong) (vertexStride * (uint) vertexCount);
		var vertexBufferDesc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = vertexBufferSize,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = Format.FormatUnknown,
			SampleDesc = new SampleDesc(1, 0),
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.None
		};

		var defaultHeapProps = new HeapProperties(HeapType.Default);
		var uploadHeapProps = new HeapProperties(HeapType.Upload);

		ComPtr<ID3D12Resource> vertexBuffer;
		SilkMarshal.ThrowHResult(
			_device.CreateCommittedResource(
				&defaultHeapProps,
				HeapFlags.None,
				in vertexBufferDesc,
				ResourceStates.CopyDest,
				null,
				out vertexBuffer));

		ComPtr<ID3D12Resource> vertexUpload;
		SilkMarshal.ThrowHResult(
			_device.CreateCommittedResource(
				&uploadHeapProps,
				HeapFlags.None,
				in vertexBufferDesc,
				ResourceStates.GenericRead,
				null,
				out vertexUpload));

		void* mappedVertices = null;
		SilkMarshal.ThrowHResult(vertexUpload.Map(0, (Range*) null, &mappedVertices));
		try
		{
			fixed (VertexData* srcVertices = vertices)
			{
				Buffer.MemoryCopy(srcVertices, mappedVertices, vertexBufferSize, vertexBufferSize);
			}
		}
		finally
		{
			vertexUpload.Unmap(0, (Range*) null);
		}

		var indices = mesh.Indices;
		if (indices.Length == 0)
		{
			throw new InvalidOperationException("Mesh must contain index data.");
		}

		var indexBufferSize = (ulong) (sizeof(uint) * indices.Length);
		var indexBufferDesc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = indexBufferSize,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = Format.FormatUnknown,
			SampleDesc = new SampleDesc(1, 0),
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.None
		};

		ComPtr<ID3D12Resource> indexBuffer;
		SilkMarshal.ThrowHResult(
			_device.CreateCommittedResource(
				&defaultHeapProps,
				HeapFlags.None,
				in indexBufferDesc,
				ResourceStates.CopyDest,
				null,
				out indexBuffer));

		ComPtr<ID3D12Resource> indexUpload;
		SilkMarshal.ThrowHResult(
			_device.CreateCommittedResource(
				&uploadHeapProps,
				HeapFlags.None,
				in indexBufferDesc,
				ResourceStates.GenericRead,
				null,
				out indexUpload));

		void* mappedIndices = null;
		SilkMarshal.ThrowHResult(indexUpload.Map(0, (Range*) null, &mappedIndices));
		try
		{
			fixed (uint* srcIndices = indices)
			{
				Buffer.MemoryCopy(srcIndices, mappedIndices, indexBufferSize, indexBufferSize);
			}
		}
		finally
		{
			indexUpload.Unmap(0, (Range*) null);
		}

		SilkMarshal.ThrowHResult(_commandAllocators[0].Reset());
		SilkMarshal.ThrowHResult(_commandList.Reset(_commandAllocators[0].Handle, (ID3D12PipelineState*) null));

		_commandList.CopyBufferRegion(vertexBuffer.Handle, 0, vertexUpload.Handle, 0, vertexBufferSize);
		_commandList.CopyBufferRegion(indexBuffer.Handle, 0, indexUpload.Handle, 0, indexBufferSize);

		var vertexBarrier = new ResourceBarrier
			{Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None};
		vertexBarrier.Anonymous.Transition = new ResourceTransitionBarrier
		{
			PResource = vertexBuffer.Handle,
			Subresource = D3D12.ResourceBarrierAllSubresources,
			StateBefore = ResourceStates.CopyDest,
			StateAfter = ResourceStates.VertexAndConstantBuffer
		};
		_commandList.ResourceBarrier(1, &vertexBarrier);

		var indexBarrier = new ResourceBarrier
			{Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None};
		indexBarrier.Anonymous.Transition = new ResourceTransitionBarrier
		{
			PResource = indexBuffer.Handle,
			Subresource = D3D12.ResourceBarrierAllSubresources,
			StateBefore = ResourceStates.CopyDest,
			StateAfter = ResourceStates.IndexBuffer
		};
		_commandList.ResourceBarrier(1, &indexBarrier);

		SilkMarshal.ThrowHResult(_commandList.Close());
		ID3D12CommandList* copyLists = (ID3D12CommandList*) _commandList.Handle;
		_commandQueue.ExecuteCommandLists(1, &copyLists);
		SignalAndWait();

		vertexUpload.Dispose();
		indexUpload.Dispose();

		var vertexView = new D3DVertexBufferView
		{
			BufferLocation = vertexBuffer.GetGPUVirtualAddress(),
			SizeInBytes = (uint) vertexBufferSize,
			StrideInBytes = vertexStride
		};

		var indexView = new D3DIndexBufferView
		{
			BufferLocation = indexBuffer.GetGPUVirtualAddress(),
			SizeInBytes = (uint) indexBufferSize,
			Format = Format.FormatR32Uint
		};

		return new MeshResources(vertexBuffer, indexBuffer, vertexView, indexView, (uint) indices.Length);
	}

	private MeshResources EnsureMeshResources(Mesh mesh)
	{
		if (_meshResources.TryGetValue(mesh, out var resources))
		{
			return resources;
		}

		resources = CreateMeshResources(mesh);
		_meshResources.Add(mesh, resources);
		return resources;
	}

	private static ulong Align(ulong size, ulong alignment)
	{
		return (size + alignment - 1) & ~(alignment - 1);
	}

	private void HandleCreateMeshCommand(RenderCommand command)
	{
		var payload = command.ReadPayload<RenderCommand.CreateMeshPayload>();
		if (payload.MeshHandle.Target is not Mesh mesh)
		{
			throw new InvalidOperationException("Mesh payload target was null.");
		}

		payload.MeshHandle.Free();
		EnsureMeshResources(mesh);
	}

	private void HandleDrawMeshCommand(RenderCommand command)
	{
		var payload = command.ReadPayload<RenderCommand.DrawMeshPayload>();
		if (payload.MeshHandle.Target is not Mesh mesh)
		{
			throw new InvalidOperationException("Mesh payload target was null.");
		}

		if (payload.MaterialHandle.Target is not Material material)
		{
			throw new InvalidOperationException("Material payload target was null.");
		}

		payload.MeshHandle.Free();
		payload.MaterialHandle.Free();
		EnsureMeshResources(mesh);
		_drawCommands.Add(new(mesh, material, payload.Transform));
	}

	private void HandleSetCameraCommand(RenderCommand command)
	{
		var payload = command.ReadPayload<RenderCommand.SetCameraPayload>();
		if (payload.CameraHandle.Target is not Camera camera)
		{
			throw new InvalidOperationException("Camera payload target was null.");
		}

		payload.CameraHandle.Free();
		_camera = camera;
		_cameraTransform = payload.Transform;
		_hasCamera = true;
	}

	private bool TryGetCameraMatrices(out Matrix4x4 viewProjection, out Vector3 position)
	{
		var world = _cameraTransform.GetTransform();

		if (Matrix4x4.Invert(world, out var view) == false ||
		    Matrix4x4.Decompose(world, out _, out _, out position) == false)
		{
			viewProjection = Matrix4x4.Identity;
			position = Vector3.Zero;
			return false;
		}

		viewProjection = view * _camera.Perspective;
		return true;
	}

	private void OnUpdate(double deltaSeconds)
	{
		ProcessPendingCommands();
	}

	private void OnFramebufferResize(Int2 newSize)
	{
		if (newSize.X == 0 || newSize.Y == 0)
		{
			return;
		}

		_framebufferSize = newSize;

		WaitForGpu();

		for (var i = 0; i < FrameCount; i++)
		{
			if (_renderTargets[i].Handle is not null)
			{
				_renderTargets[i].Dispose();
			}
		}

		if (_rtvHeap.Handle is not null)
		{
			_rtvHeap.Dispose();
		}

		SilkMarshal.ThrowHResult(_swapchain.ResizeBuffers(FrameCount, (uint) newSize.X, (uint) newSize.Y,
			Format.FormatB8G8R8A8Unorm, 0));
		_backbufferIndex = _swapchain.GetCurrentBackBufferIndex();

		CreateRtvHeapAndTargets();
		CreateDepthResources();
		CreateLightingBuffer();
	}

	public void BeginFrame()
	{
		var frameIdx = _backbufferIndex;

		if (_fence.GetCompletedValue() < _frameFenceValues[frameIdx])
		{
			SilkMarshal.ThrowHResult(_fence.SetEventOnCompletion(_frameFenceValues[frameIdx], (void*) _fenceEvent));
			WaitForSingleObject(_fenceEvent, 0xFFFFFFFF);
		}

		if (_fence.GetCompletedValue() < _fenceValue)
		{
			SilkMarshal.ThrowHResult(_fence.SetEventOnCompletion(_fenceValue, (void*) _fenceEvent));
			WaitForSingleObject(_fenceEvent, 0xFFFFFFFF);
		}

		var gfxCommandList = _gfxDevice.BeginGraphics() as ID3D12BackendCommandList;

		var commandList = gfxCommandList.NativeCommandList;
		_activeCommandList = commandList;
		_currentGfxCommandList = gfxCommandList;
	}

	public void Render(float deltaTime, RenderGraphResourceRegistry resourceRegistry, RenderGraphResourceHandle backBuffer, RenderGraphResourceHandle depthTexture)
	{
		var framebufferSize = _framebufferSize;

		// var frameResources = renderGraphFrameBuilder.BeginFrame(framebufferSize, ImportBackbuffer, ImportDepth);
		//
		// var callbacks = new RenderPassCallbacks(
		// 	(context, resources) => ExecuteGBufferPass(context, resources),
		// 	(context, resources) => ExecuteDeferredPass(context, resources));
		//
		// var renderedScene = renderGraphFrameBuilder.BuildAndExecute(callbacks);
		//
		var backbufferResource = resourceRegistry.GetTexture(backBuffer);
		var backbufferTexture = backbufferResource as ID3D12BackendTexture
		                        ?? throw new InvalidOperationException(
			                        "Render graph returned a texture incompatible with the Direct3D12 backend.");
		// renderGraphFrameBuilder.EndFrame();

		var barrierEnd = new ResourceBarrier
			{Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None};
		barrierEnd.Anonymous.Transition = new()
		{
			PResource = backbufferTexture.Resource,
			Subresource = D3D12.ResourceBarrierAllSubresources,
			StateBefore = ResourceStates.RenderTarget,
			StateAfter = ResourceStates.Present
		};
		_activeCommandList->ResourceBarrier(1, &barrierEnd);

		_gfxDevice.Submit(_currentGfxCommandList);
		_activeCommandList = null;
		_currentGfxCommandList = null!;

		var presentResult = _swapchain.Present(1, 0);
		if (presentResult < 0)
		{
			var removalReason = _device.GetDeviceRemovedReason();
			var message =
				$"IDXGISwapChain::Present failed with HRESULT 0x{presentResult:X8}. DeviceRemovedReason=0x{removalReason:X8}.";

			throw new InvalidOperationException(message);
		}

		_fenceValue++;
		SilkMarshal.ThrowHResult(_commandQueue.Signal(_fence, _fenceValue));
		_frameFenceValues[_backbufferIndex] = _fenceValue;

		_backbufferIndex = _swapchain.GetCurrentBackBufferIndex();
		
		_drawCommands.Clear();
		_arenaAllocator.Reset();
	}

	public RenderGraphResourceHandle ImportBackbuffer(RenderGraphResourceRegistry registry,
		int width, int height)
	{
		var descriptor = new TextureDescriptor(
			Math.Max(width, 1),
			Math.Max(height, 1),
			TextureFormat.Bgra8Unorm,
			TextureUsage.RenderTarget);

		var imported = _gfxDevice.ImportExternalTexture(
			descriptor,
			_renderTargets[_backbufferIndex].Handle,
			_rtvCpuHandles[_backbufferIndex],
			null);

		return registry.ImportTexture(imported, takeOwnership: false);
	}

	public RenderGraphResourceHandle ImportDepthTexture(RenderGraphResourceRegistry registry, int width, int height)
	{
		if (_depthBuffer.Handle is null)
		{
			throw new InvalidOperationException("Depth buffer was not initialised.");
		}

		var descriptor = new TextureDescriptor(
			Math.Max(width, 1),
			Math.Max(height, 1),
			TextureFormat.D32Float,
			TextureUsage.DepthStencil);

		var depthHandle = _dsvHeap.GetCPUDescriptorHandleForHeapStart();

		var imported = _gfxDevice.ImportExternalTexture(
			descriptor,
			_depthBuffer.Handle,
			null,
			depthHandle);

		return registry.ImportTexture(imported, takeOwnership: false);
	}

	public void ExecuteGBufferPass(RenderGraphContext context, RenderGraphFrameResources resources)
	{
		var canRender = _drawCommands.Count > 0 && _hasCamera;
		if (canRender == false)
		{
			return;
		}

		if (TryGetCameraMatrices(out var viewProjection, out var cameraPosition) == false)
		{
			return;
		}

		var albedoTexture = context.GetTexture(resources.GBufferAlbedo) as ID3D12BackendTexture
		                    ?? throw new InvalidOperationException(
			                    "Albedo texture is not compatible with the Direct3D12 backend.");
		var normalTexture = context.GetTexture(resources.GBufferNormal) as ID3D12BackendTexture
		                    ?? throw new InvalidOperationException(
			                    "Normal texture is not compatible with the Direct3D12 backend.");
		var materialTexture = context.GetTexture(resources.GBufferMaterial) as ID3D12BackendTexture
		                      ?? throw new InvalidOperationException(
			                      "Material texture is not compatible with the Direct3D12 backend.");
		var depthTexture = context.GetTexture(resources.GBufferDepth) as ID3D12BackendTexture
		                   ?? throw new InvalidOperationException(
			                   "Depth texture is not compatible with the Direct3D12 backend.");

		var gbufferConfig = new GBufferPassConfig
		{
			FramebufferWidth = resources.FramebufferSize.X,
			FramebufferHeight = resources.FramebufferSize.Y,
			AlbedoTarget = albedoTexture,
			NormalTarget = normalTexture,
			MaterialTarget = materialTexture,
			DepthTarget = depthTexture,
			AlbedoClearColor = _backgroundColour
		};

		GBufferPass.Record(_currentGfxCommandList, gbufferConfig, () =>
		{
			_activeCommandList->SetPipelineState(_gbufferPipeline.Handle);
			_activeCommandList->SetGraphicsRootSignature(_rootSignature.Handle);

			Span<float> cameraConstants = stackalloc float[20];
			WriteMatrix(cameraConstants, viewProjection);
			cameraConstants[16] = cameraPosition.X;
			cameraConstants[17] = cameraPosition.Y;
			cameraConstants[18] = cameraPosition.Z;
			cameraConstants[19] = 1.0f;

#pragma warning disable CA2014
			foreach (var draw in _drawCommands)
			{
				var meshResources = EnsureMeshResources(draw.Mesh);
				var materialResources = draw.Material.Resources as D3D12MaterialResources;

				// ReSharper disable once PossibleNullReferenceException (accepting the risk here so we don't have to assert in the render loop)
				var colorBufferPtr = materialResources.ColorBuffer.Handle;
				_activeCommandList->SetGraphicsRootConstantBufferView(0, colorBufferPtr->GetGPUVirtualAddress());

				Span<float> modelConstants = stackalloc float[16];
				WriteMatrix(modelConstants, draw.Transform);
				fixed (float* modelPtr = modelConstants)
				{
					_activeCommandList->SetGraphicsRoot32BitConstants(1, 16, modelPtr, 0);
				}

				fixed (float* cameraPtr = cameraConstants)
				{
					_activeCommandList->SetGraphicsRoot32BitConstants(2, 20, cameraPtr, 0);
				}

				_activeCommandList->IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
				var vertexView = meshResources.VertexView;
				_activeCommandList->IASetVertexBuffers(0, 1, &vertexView);
				var indexView = meshResources.IndexView;
				_activeCommandList->IASetIndexBuffer(&indexView);
				_activeCommandList->DrawIndexedInstanced(meshResources.IndexCount, 1, 0, 0, 0);
			}
#pragma warning restore CA2014
		});
	}

	public void ExecuteDeferredPass(RenderGraphContext context, RenderGraphFrameResources resources)
	{
		if (_lightingBuffer.Handle is null)
		{
			return;
		}

		if (_hasCamera == false)
		{
			_drawCommands.Clear();
			return;
		}

		var gbufferAlbedo = context.GetTexture(resources.GBufferAlbedo) as ID3D12BackendTexture;
		var gbufferNormal = context.GetTexture(resources.GBufferNormal) as ID3D12BackendTexture;
		var gbufferMaterial = context.GetTexture(resources.GBufferMaterial) as ID3D12BackendTexture;
		var backbufferTextureDeferred = context.GetTexture(resources.Backbuffer) as ID3D12BackendTexture;

		var resourceBarriers = stackalloc ResourceBarrier[3];
		resourceBarriers[0] = new ResourceBarrier
			{Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None};
		resourceBarriers[0].Anonymous.Transition = new ResourceTransitionBarrier
		{
			PResource = gbufferAlbedo.Resource,
			Subresource = D3D12.ResourceBarrierAllSubresources,
			StateBefore = ResourceStates.RenderTarget,
			StateAfter = ResourceStates.NonPixelShaderResource
		};
		resourceBarriers[1] = new ResourceBarrier
			{Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None};
		resourceBarriers[1].Anonymous.Transition = new ResourceTransitionBarrier
		{
			PResource = gbufferNormal.Resource,
			Subresource = D3D12.ResourceBarrierAllSubresources,
			StateBefore = ResourceStates.RenderTarget,
			StateAfter = ResourceStates.NonPixelShaderResource
		};
		resourceBarriers[2] = new ResourceBarrier
			{Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None};
		resourceBarriers[2].Anonymous.Transition = new ResourceTransitionBarrier
		{
			PResource = gbufferMaterial.Resource,
			Subresource = D3D12.ResourceBarrierAllSubresources,
			StateBefore = ResourceStates.RenderTarget,
			StateAfter = ResourceStates.NonPixelShaderResource
		};
		_activeCommandList->ResourceBarrier(3, resourceBarriers);

		var cpuHandle = _lightingDescriptorHeap.GetCPUDescriptorHandleForHeapStart();
		_device.CreateShaderResourceView(gbufferAlbedo.Resource, (ShaderResourceViewDesc*) null, cpuHandle);
		cpuHandle.Ptr += _lightingDescriptorSize;
		_device.CreateShaderResourceView(gbufferNormal.Resource, (ShaderResourceViewDesc*) null, cpuHandle);
		cpuHandle.Ptr += _lightingDescriptorSize;
		_device.CreateShaderResourceView(gbufferMaterial.Resource, (ShaderResourceViewDesc*) null, cpuHandle);
		cpuHandle.Ptr += _lightingDescriptorSize;

		_device.CreateUnorderedAccessView(_lightingBuffer.Handle, (ID3D12Resource*) null,
			(UnorderedAccessViewDesc*) null,
			cpuHandle);

		ID3D12DescriptorHeap* descriptorHeaps = _lightingDescriptorHeap.Handle;
		_activeCommandList->SetDescriptorHeaps(1, &descriptorHeaps);

		var srvGpuHandle = _lightingDescriptorHeap.GetGPUDescriptorHandleForHeapStart();
		var uavGpuHandle = srvGpuHandle;
		uavGpuHandle.Ptr += _lightingDescriptorSize * 3;

		_activeCommandList->SetPipelineState(_lightingPipeline.Handle);
		_activeCommandList->SetComputeRootSignature(_lightingRootSignature.Handle);
		_activeCommandList->SetComputeRootDescriptorTable(0, srvGpuHandle);
		_activeCommandList->SetComputeRootDescriptorTable(1, uavGpuHandle);

		if (TryGetCameraMatrices(out var viewProjection, out var cameraPosition) == false)
		{
			return;
		}

		Span<float> cameraConstants = stackalloc float[20];
		WriteMatrix(cameraConstants, viewProjection);
		cameraConstants[16] = cameraPosition.X;
		cameraConstants[17] = cameraPosition.Y;
		cameraConstants[18] = cameraPosition.Z;
		cameraConstants[19] = 1.0f;

		fixed (float* cameraPtr = cameraConstants)
		{
			_activeCommandList->SetComputeRoot32BitConstants(2, 20, cameraPtr, 0);
		}

		var dispatchX = (uint) ((resources.FramebufferSize.X + 7) / 8);
		var dispatchY = (uint) ((resources.FramebufferSize.Y + 7) / 8);
		_activeCommandList->Dispatch(dispatchX, dispatchY, 1);

		var uavBarrier = new ResourceBarrier {Type = ResourceBarrierType.Uav, Flags = ResourceBarrierFlags.None};
		uavBarrier.Anonymous.UAV.PResource = _lightingBuffer.Handle;
		_activeCommandList->ResourceBarrier(1, &uavBarrier);

		var copyBarriers = stackalloc ResourceBarrier[2];
		copyBarriers[0] = new ResourceBarrier
			{Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None};
		copyBarriers[0].Anonymous.Transition = new ResourceTransitionBarrier
		{
			PResource = _lightingBuffer.Handle,
			Subresource = D3D12.ResourceBarrierAllSubresources,
			StateBefore = ResourceStates.UnorderedAccess,
			StateAfter = ResourceStates.CopySource
		};
		copyBarriers[1] = new ResourceBarrier
			{Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None};
		copyBarriers[1].Anonymous.Transition = new ResourceTransitionBarrier
		{
			PResource = backbufferTextureDeferred.Resource,
			Subresource = D3D12.ResourceBarrierAllSubresources,
			StateBefore = ResourceStates.Present,
			StateAfter = ResourceStates.CopyDest
		};
		_activeCommandList->ResourceBarrier(2, copyBarriers);

		_activeCommandList->CopyResource(backbufferTextureDeferred.Resource, _lightingBuffer.Handle);

		var presentBarriers = stackalloc ResourceBarrier[2];
		presentBarriers[0] = new ResourceBarrier
			{Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None};
		presentBarriers[0].Anonymous.Transition = new ResourceTransitionBarrier
		{
			PResource = backbufferTextureDeferred.Resource,
			Subresource = D3D12.ResourceBarrierAllSubresources,
			StateBefore = ResourceStates.CopyDest,
			StateAfter = ResourceStates.Present
		};
		presentBarriers[1] = new ResourceBarrier
			{Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None};
		presentBarriers[1].Anonymous.Transition = new ResourceTransitionBarrier
		{
			PResource = _lightingBuffer.Handle,
			Subresource = D3D12.ResourceBarrierAllSubresources,
			StateBefore = ResourceStates.CopySource,
			StateAfter = ResourceStates.UnorderedAccess
		};
		_activeCommandList->ResourceBarrier(2, presentBarriers);
	}

	private void SignalAndWait()
	{
		_fenceValue++;
		SilkMarshal.ThrowHResult(_commandQueue.Signal(_fence, _fenceValue));
		if (_fence.GetCompletedValue() < _fenceValue)
		{
			SilkMarshal.ThrowHResult(_fence.SetEventOnCompletion(_fenceValue, (void*) _fenceEvent));
			WaitForSingleObject(_fenceEvent, 0xFFFFFFFF);
		}
	}

	private void WaitForGpu()
	{
		_fenceValue++;
		SilkMarshal.ThrowHResult(_commandQueue.Signal(_fence, _fenceValue));
		SilkMarshal.ThrowHResult(_fence.SetEventOnCompletion(_fenceValue, (void*) _fenceEvent));
		WaitForSingleObject(_fenceEvent, 0xFFFFFFFF);
	}

	private void Dispose()
	{
		if (_commandQueue.Handle is not null && _fence.Handle is not null)
		{
			SignalAndWait();
		}

		for (var i = 0; i < FrameCount; i++)
		{
			_renderTargets[i].Dispose();
			_commandAllocators[i].Dispose();
		}

		_commandList.Dispose();
		_rtvHeap.Dispose();
		if (_dsvHeap.Handle is not null)
		{
			_dsvHeap.Dispose();
			_dsvHeap = default;
		}

		_factory.Dispose();
		_swapchain.Dispose();
		_commandQueue.Dispose();
		if (_depthBuffer.Handle is not null)
		{
			_depthBuffer.Dispose();
			_depthBuffer = default;
		}

		foreach (var meshResources in _meshResources.Values)
		{
			if (meshResources.VertexBuffer.Handle is not null)
			{
				meshResources.VertexBuffer.Dispose();
			}

			if (meshResources.IndexBuffer.Handle is not null)
			{
				meshResources.IndexBuffer.Dispose();
			}
		}

		_meshResources.Clear();

		// TODO: material disposal should be handled by render graph

		if (_gbufferPipeline.Handle is not null)
		{
			_gbufferPipeline.Dispose();
			_gbufferPipeline = default;
		}

		if (_lightingPipeline.Handle is not null)
		{
			_lightingPipeline.Dispose();
			_lightingPipeline = default;
		}

		if (_lightingRootSignature.Handle is not null)
		{
			_lightingRootSignature.Dispose();
			_lightingRootSignature = default;
		}

		if (_lightingDescriptorHeap.Handle is not null)
		{
			_lightingDescriptorHeap.Dispose();
			_lightingDescriptorHeap = default;
		}

		if (_lightingBuffer.Handle is not null)
		{
			_lightingBuffer.Dispose();
			_lightingBuffer = default;
		}

		if (_rootSignature.Handle is not null)
		{
			_rootSignature.Dispose();
			_rootSignature = default;
		}

		if (_fence.Handle is not null)
		{
			_fence.Dispose();
			_fence = default;
		}

		_device.Dispose();
		_d3d12.Dispose();
		_dxgi.Dispose();

		if (_fenceEvent != nint.Zero)
		{
			CloseHandle(_fenceEvent);
			_fenceEvent = nint.Zero;
		}

		if (_inputContext is not null)
		{
			foreach (var keyboard in _inputContext.Keyboards)
			{
				keyboard.KeyDown -= HandleKeyDown;
			}

			//_inputContext.Dispose();
			_inputContext = null;
		}

		if (_window is not null)
		{
			_window.Load -= OnWindowLoad;
			_window.Update -= OnWindowUpdate;
			_window.Render -= OnWindowRender;
			_window.FramebufferResize -= OnWindowFramebufferResize;
			_window.Closing -= OnWindowClosing;
			_window.Dispose();
			_window = null;
		}

		_isInitialized = false;
	}

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern nint CreateEventEx(nint lpEventAttributes, string lpName, uint dwFlags, uint dwDesiredAccess);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(nint hObject);
}
