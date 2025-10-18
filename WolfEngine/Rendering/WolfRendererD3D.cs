using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using Silk.NET.SDL;

namespace WolfEngine;

public unsafe class WolfRendererD3D : IRenderer
{
	private const int FrameCount = 2;
	private const uint SdlQuitEvent = 0x100;
	private const uint SdlWindowEvent = 0x200;
	private const uint SdlKeyDownEvent = 0x300;

	private readonly float[] _backgroundColour = [0.392f, 0.584f, 0.929f, 1.0f];

	private sealed class MaterialResources
	{
		public MaterialResources(ComPtr<ID3D12PipelineState> pipelineState, ComPtr<ID3D12Resource> colorBuffer)
		{
			PipelineState = pipelineState;
			ColorBuffer = colorBuffer;
		}

		public ComPtr<ID3D12PipelineState> PipelineState { get; }

		public ComPtr<ID3D12Resource> ColorBuffer { get; }
	}

	private sealed class MeshResources
	{
		public MeshResources(
			ComPtr<ID3D12Resource> vertexBuffer,
			ComPtr<ID3D12Resource> indexBuffer,
			VertexBufferView vertexView,
			IndexBufferView indexView,
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

		public VertexBufferView VertexView { get; }

		public IndexBufferView IndexView { get; }

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
	private readonly Sdl _sdl;

	private DXGI _dxgi = null!;
	private D3D12 _d3d12 = null!;
	private ComPtr<IDXGIFactory2> _factory = default;
	private ComPtr<IDXGISwapChain3> _swapchain = default;
	private ComPtr<ID3D12Device> _device = default;
	private ComPtr<IDXGIAdapter> _adapter = default;
	private ComPtr<ID3D12CommandQueue> _commandQueue = default;

	private ComPtr<ID3D12DescriptorHeap> _rtvHeap = default;
	private uint _rtvDescriptorSize;
	private readonly CpuDescriptorHandle[] _rtvCpuHandles = new CpuDescriptorHandle[FrameCount];
	private readonly ulong[] _frameFenceValues = new ulong[FrameCount];
	private readonly ComPtr<ID3D12Resource>[] _renderTargets = new ComPtr<ID3D12Resource>[FrameCount];

	private readonly ComPtr<ID3D12CommandAllocator>[] _commandAllocators =
		new ComPtr<ID3D12CommandAllocator>[FrameCount];

	private ComPtr<ID3D12GraphicsCommandList> _commandList = default;
	private ComPtr<ID3D12Fence> _fence = default;
	private ulong _fenceValue;
	private nint _fenceEvent = nint.Zero;
	private ComPtr<ID3D12Resource> _vertexBuffer = default;
	private ComPtr<ID3D12Resource> _vertexBufferUpload = default;
	private VertexBufferView _vertexBufferView;
	private ComPtr<ID3D12RootSignature> _rootSignature = default;
	private ComPtr<ID3D12DescriptorHeap> _dsvHeap = default;
	private ComPtr<ID3D12Resource> _depthBuffer = default;
	private readonly ConcurrentQueue<RenderCommand> _pendingCommands = new();
	private readonly Dictionary<Mesh, MeshResources> _meshResources = new();
	private readonly Dictionary<Material, MaterialResources> _materialResources = new();
	private readonly List<DrawInstruction> _drawCommands = new();
	private Camera _camera = null!;
	private bool _hasCamera;
	private Material _triangleMaterial = null!;

	private uint _backbufferIndex;
	private Window* _window;
	private nint _windowHandle;
	private bool _isRunning;
	private Vector2D<int> _framebufferSize;

	public WolfRendererD3D(IShaderCompiler shaderCompiler)
	{
		_width = 1280;
		_height = 720;
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_sdl = Sdl.GetApi();
	}

	public void Run(Action startup, Action update)
	{
		var startupCallback = startup ?? throw new ArgumentNullException(nameof(startup));
		var updateCallback = update ?? throw new ArgumentNullException(nameof(update));

		InitializeWindow();
		OnLoad();

		_isRunning = true;
		var evt = new Event();
		var clock = Stopwatch.StartNew();
		var lastTime = 0.0;

		startupCallback();

		try
		{
			while (_isRunning)
			{
				PumpEvents(ref evt);

				updateCallback();

				var currentTime = clock.Elapsed.TotalSeconds;
				var delta = currentTime - lastTime;
				lastTime = currentTime;

				OnUpdate(delta);
				if (_framebufferSize.X > 0 && _framebufferSize.Y > 0)
				{
					OnRender(delta);
				}
				else
				{
					_sdl.Delay(1);
				}
			}
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

	private void InitializeWindow()
	{
		if (_sdl.Init(Sdl.InitVideo) < 0)
		{
			throw new InvalidOperationException("Failed to initialise SDL video subsystem.");
		}

		var titlePtr = SilkMarshal.StringToPtr("WolfEngine", NativeStringEncoding.UTF8);
		try
		{
			var flags = WindowFlags.Resizable;
			_window = _sdl.CreateWindow((byte*) titlePtr, Sdl.WindowposCentered, Sdl.WindowposCentered, _width, _height,
				(uint) flags);
		}
		finally
		{
			SilkMarshal.Free(titlePtr);
		}

		if (_window is null)
		{
			throw new InvalidOperationException("Failed to create SDL window.");
		}

		RetrieveNativeHandle();
		UpdateFramebufferSize();
	}

	private void RetrieveNativeHandle()
	{
		var wmInfo = new SysWMInfo();
		_sdl.GetVersion(&wmInfo.Version);

		if (!_sdl.GetWindowWMInfo(_window, &wmInfo))
		{
			throw new InvalidOperationException("Failed to query SDL window information.");
		}

		if (wmInfo.Subsystem != SysWMType.Windows)
		{
			throw new InvalidOperationException($"Unsupported SDL subsystem {wmInfo.Subsystem} for Direct3D renderer.");
		}

		_windowHandle = (nint) wmInfo.Info.Win.Hwnd;
		if (_windowHandle == nint.Zero)
		{
			throw new InvalidOperationException("SDL reported a null window handle.");
		}
	}

	private void PumpEvents(ref Event evt)
	{
		while (_sdl.PollEvent(ref evt) != 0)
		{
			switch (evt.Type)
			{
				case SdlQuitEvent:
					_isRunning = false;
					break;
				case SdlWindowEvent:
					HandleWindowEvent(evt);
					break;
				case SdlKeyDownEvent:
					HandleKeyEvent(evt);
					break;
			}
		}
	}

	private void HandleWindowEvent(Event evt)
	{
		switch ((WindowEventID) evt.Window.Event)
		{
			case WindowEventID.Close:
				_isRunning = false;
				break;
			case WindowEventID.SizeChanged:
			case WindowEventID.Resized:
				var width = evt.Window.Data1;
				var height = evt.Window.Data2;
				if (width > 0 && height > 0)
				{
					var newSize = new Vector2D<int>(width, height);
					OnFramebufferResize(newSize);
				}

				break;
		}
	}

	private void HandleKeyEvent(Event evt)
	{
		var keySym = evt.Key.Keysym;
		const int EscapeKeycode = 27;
		if (keySym.Sym == EscapeKeycode)
		{
			_isRunning = false;
		}
	}

	private void UpdateFramebufferSize()
	{
		if (_window is null)
		{
			return;
		}

		int width = 0;
		int height = 0;
		_sdl.GetWindowSize(_window, ref width, ref height);
		if (width > 0 && height > 0)
		{
			_framebufferSize = new Vector2D<int>(width, height);
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


	private void CreateDepthResources()
	{
		if (_framebufferSize.X <= 0 || _framebufferSize.Y <= 0)
		{
			_framebufferSize = new Vector2D<int>(_width, _height);
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
				case RenderCommandType.CreateMaterial:
					HandleCreateMaterialCommand(command);
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

	private MaterialResources CreateMaterialResources(Material material)
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
			FillMode = FillMode.Solid,
			CullMode = CullMode.Back,
			FrontCounterClockwise = 1,
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
				PRootSignature = (ID3D12RootSignature*) _rootSignature.Handle,
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

		var colorSize = Align((ulong) Unsafe.SizeOf<Vector4>(), D3D12.ConstantBufferDataPlacementAlignment);
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
		SilkMarshal.ThrowHResult(colorBuffer.Map(0, (Silk.NET.Direct3D12.Range*) null, &mappedData));
		try
		{
			var color = material.Color;
			Unsafe.Write((Vector4*) mappedData, color);
		}
		finally
		{
			colorBuffer.Unmap(0, (Silk.NET.Direct3D12.Range*) null);
		}

		return new MaterialResources(pipelineState, colorBuffer);
	}

	private MaterialResources EnsureMaterialResources(Material material)
	{
		if (_materialResources.TryGetValue(material, out var resources))
		{
			return resources;
		}

		resources = CreateMaterialResources(material);
		_materialResources.Add(material, resources);
		return resources;
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
		if (mesh is null)
		{
			throw new ArgumentNullException(nameof(mesh));
		}

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
		SilkMarshal.ThrowHResult(vertexUpload.Map(0, (Silk.NET.Direct3D12.Range*) null, &mappedVertices));
		try
		{
			fixed (VertexData* srcVertices = vertices)
			{
				System.Buffer.MemoryCopy(srcVertices, mappedVertices, vertexBufferSize, vertexBufferSize);
			}
		}
		finally
		{
			vertexUpload.Unmap(0, (Silk.NET.Direct3D12.Range*) null);
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
		SilkMarshal.ThrowHResult(indexUpload.Map(0, (Silk.NET.Direct3D12.Range*) null, &mappedIndices));
		try
		{
			fixed (uint* srcIndices = indices)
			{
				System.Buffer.MemoryCopy(srcIndices, mappedIndices, indexBufferSize, indexBufferSize);
			}
		}
		finally
		{
			indexUpload.Unmap(0, (Silk.NET.Direct3D12.Range*) null);
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

		var vertexView = new VertexBufferView
		{
			BufferLocation = vertexBuffer.GetGPUVirtualAddress(),
			SizeInBytes = (uint) vertexBufferSize,
			StrideInBytes = vertexStride
		};

		var indexView = new IndexBufferView
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

	private void HandleCreateMaterialCommand(RenderCommand command)
	{
		var payload = command.ReadPayload<RenderCommand.CreateMaterialPayload>();
		if (payload.MaterialHandle.Target is not Material material)
		{
			throw new InvalidOperationException("Material payload target was null.");
		}

		payload.MaterialHandle.Free();
		EnsureMaterialResources(material);
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
		EnsureMaterialResources(material);
		_drawCommands.Add(new DrawInstruction(mesh, material, payload.Transform));
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
		_hasCamera = true;
	}

	private void OnUpdate(double deltaSeconds)
	{
		ProcessPendingCommands();
	}

	private void OnFramebufferResize(Vector2D<int> newSize)
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
	}

	private void OnRender(double deltaSeconds)
	{
		var frameIdx = _backbufferIndex;

		if (_fence.GetCompletedValue() < _frameFenceValues[frameIdx])
		{
			SilkMarshal.ThrowHResult(_fence.SetEventOnCompletion(_frameFenceValues[frameIdx], (void*) _fenceEvent));
			WaitForSingleObject(_fenceEvent, 0xFFFFFFFF);
		}

		SilkMarshal.ThrowHResult(_commandAllocators[frameIdx].Reset());
		SilkMarshal.ThrowHResult(_commandList.Reset(_commandAllocators[frameIdx].Handle, (ID3D12PipelineState*) null));

		var barrierBegin = new ResourceBarrier
			{Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None};
		barrierBegin.Anonymous.Transition = new()
		{
			PResource = _renderTargets[frameIdx].Handle,
			Subresource = D3D12.ResourceBarrierAllSubresources,
			StateBefore = ResourceStates.Present,
			StateAfter = ResourceStates.RenderTarget
		};
		_commandList.ResourceBarrier(1, &barrierBegin);

		var fb = _framebufferSize;
		var vp = new Viewport
			{TopLeftX = 0, TopLeftY = 0, Width = fb.X, Height = fb.Y, MinDepth = 0.0f, MaxDepth = 1.0f};
		_commandList.RSSetViewports(1, &vp);
		var sc = new Box2D<int>(0, 0, fb.X, fb.Y);
		_commandList.RSSetScissorRects(1, &sc);

		var rtvHandle = _rtvCpuHandles[frameIdx];
		var dsvHandle = _dsvHeap.GetCPUDescriptorHandleForHeapStart();
		_commandList.OMSetRenderTargets(1, &rtvHandle, new(false), &dsvHandle);
		fixed (float* clear = _backgroundColour)
		{
			_commandList.ClearRenderTargetView(rtvHandle, clear, 0, (Box2D<int>*) null);
		}

		_commandList.ClearDepthStencilView(dsvHandle, ClearFlags.Depth, 1.0f, 0, 0, (Box2D<int>*) null);

		var hasDrawCommands = _drawCommands.Count > 0;
		var renderedScene = false;

#pragma warning disable CA2014
		if (hasDrawCommands && _hasCamera)
		{
			var viewProjection = _camera.Transform * _camera.Perspective;
			Span<float> cameraConstants = stackalloc float[20];
			WriteMatrix(cameraConstants, viewProjection);
			cameraConstants[16] = _camera.Position.X;
			cameraConstants[17] = _camera.Position.Y;
			cameraConstants[18] = _camera.Position.Z;
			cameraConstants[19] = 1.0f;

			foreach (var draw in _drawCommands)
			{
				var meshResources = EnsureMeshResources(draw.Mesh);
				var materialResources = EnsureMaterialResources(draw.Material);

				_commandList.SetPipelineState((ID3D12PipelineState*) materialResources.PipelineState.Handle);
				_commandList.SetGraphicsRootSignature(_rootSignature.Handle);

				var colorBufferPtr = materialResources.ColorBuffer.Handle;
				_commandList.SetGraphicsRootConstantBufferView(0, colorBufferPtr->GetGPUVirtualAddress());

				Span<float> modelConstants = stackalloc float[16];
				WriteMatrix(modelConstants, draw.Transform);
				fixed (float* modelPtr = modelConstants)
				{
					_commandList.SetGraphicsRoot32BitConstants(1, 16, modelPtr, 0);
				}

				fixed (float* cameraPtr = cameraConstants)
				{
					_commandList.SetGraphicsRoot32BitConstants(2, 20, cameraPtr, 0);
				}

				_commandList.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
				var vertexView = meshResources.VertexView;
				_commandList.IASetVertexBuffers(0, 1, &vertexView);
				var indexView = meshResources.IndexView;
				_commandList.IASetIndexBuffer(&indexView);
				_commandList.DrawIndexedInstanced(meshResources.IndexCount, 1, 0, 0, 0);
			}

			renderedScene = true;
		}
		else if (!hasDrawCommands)
		{
			var materialResources = EnsureMaterialResources(_triangleMaterial);
			_commandList.SetPipelineState((ID3D12PipelineState*) materialResources.PipelineState.Handle);
			_commandList.SetGraphicsRootSignature(_rootSignature.Handle);
			var colorBufferPtr = materialResources.ColorBuffer.Handle;
			_commandList.SetGraphicsRootConstantBufferView(0, colorBufferPtr->GetGPUVirtualAddress());

			Span<float> identityModel = stackalloc float[16];
			WriteMatrix(identityModel, Matrix4x4.Identity);
			fixed (float* modelPtr = identityModel)
			{
				_commandList.SetGraphicsRoot32BitConstants(1, 16, modelPtr, 0);
			}

			Span<float> identityCamera = stackalloc float[20];
			WriteMatrix(identityCamera, Matrix4x4.Identity);
			identityCamera[16] = 0.0f;
			identityCamera[17] = 0.0f;
			identityCamera[18] = 0.0f;
			identityCamera[19] = 1.0f;
			fixed (float* cameraPtr = identityCamera)
			{
				_commandList.SetGraphicsRoot32BitConstants(2, 20, cameraPtr, 0);
			}

			_commandList.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
			var vbView = _vertexBufferView;
			_commandList.IASetVertexBuffers(0, 1, &vbView);
			_commandList.DrawInstanced(3, 1, 0, 0);
		}
		else
		{
			_drawCommands.Clear();
		}
#pragma warning restore CA2014

		var barrierEnd = new ResourceBarrier {Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None};
		barrierEnd.Anonymous.Transition = new()
		{
			PResource = _renderTargets[frameIdx].Handle,
			Subresource = D3D12.ResourceBarrierAllSubresources,
			StateBefore = ResourceStates.RenderTarget,
			StateAfter = ResourceStates.Present
		};
		_commandList.ResourceBarrier(1, &barrierEnd);

		SilkMarshal.ThrowHResult(_commandList.Close());
		ID3D12CommandList* lists = (ID3D12CommandList*) _commandList.Handle;
		_commandQueue.ExecuteCommandLists(1, &lists);

		SilkMarshal.ThrowHResult(_swapchain.Present(1, 0));

		_fenceValue++;
		SilkMarshal.ThrowHResult(_commandQueue.Signal(_fence, _fenceValue));
		_frameFenceValues[frameIdx] = _fenceValue;

		_backbufferIndex = _swapchain.GetCurrentBackBufferIndex();
		if (renderedScene)
		{
			_drawCommands.Clear();
		}
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
		SignalAndWait();

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
		foreach (var resources in _materialResources.Values)
		{
			if (resources.PipelineState.Handle is not null)
			{
				resources.PipelineState.Dispose();
			}

			if (resources.ColorBuffer.Handle is not null)
			{
				resources.ColorBuffer.Dispose();
			}
		}

		_materialResources.Clear();
		if (_rootSignature.Handle is not null)
		{
			_rootSignature.Dispose();
			_rootSignature = default;
		}

		if (_vertexBuffer.Handle is not null)
		{
			_vertexBuffer.Dispose();
			_vertexBuffer = default;
		}

		if (_vertexBufferUpload.Handle is not null)
		{
			_vertexBufferUpload.Dispose();
			_vertexBufferUpload = default;
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

		if (_window is not null)
		{
			_sdl.DestroyWindow(_window);
			_window = null;
		}

		_sdl.Quit();
	}

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern nint CreateEventEx(nint lpEventAttributes, string lpName, uint dwFlags, uint dwDesiredAccess);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(nint hObject);
}