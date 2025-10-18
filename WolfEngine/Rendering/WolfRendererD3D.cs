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

public unsafe class WolfRendererD3D
{
    private const int FrameCount = 2;
    private const uint SdlQuitEvent = 0x100;
    private const uint SdlWindowEvent = 0x200;
    private const uint SdlKeyDownEvent = 0x300;

    private readonly float[] _backgroundColour = [0.392f, 0.584f, 0.929f, 1.0f];
    private readonly Vector4[] _triangleVertices =
    [
        new(-0.5f, -0.5f, 0.0f, 1.0f),
        new( 0.5f, -0.5f, 0.0f, 1.0f),
        new( 0.0f,  0.5f, 0.0f, 1.0f)
    ];
    private const string TriangleShaderFile = "triangle.slang";

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
    private readonly ComPtr<ID3D12CommandAllocator>[] _commandAllocators = new ComPtr<ID3D12CommandAllocator>[FrameCount];
    private ComPtr<ID3D12GraphicsCommandList> _commandList = default;
    private ComPtr<ID3D12Fence> _fence = default;
    private ulong _fenceValue;
    private nint _fenceEvent = nint.Zero;
    private ComPtr<ID3D12Resource> _vertexBuffer = default;
    private ComPtr<ID3D12Resource> _vertexBufferUpload = default;
    private VertexBufferView _vertexBufferView;
    private ComPtr<ID3D12RootSignature> _rootSignature = default;
    private ComPtr<ID3D12PipelineState> _pipelineState = default;

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

        try
        {
            InitializeWindow();
            OnLoad();
            RunLoop();
        }
        finally
        {
            Dispose();
        }
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
            _window = _sdl.CreateWindow((byte*)titlePtr, Sdl.WindowposCentered, Sdl.WindowposCentered, _width, _height, (uint)flags);
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

        _windowHandle = (nint)wmInfo.Info.Win.Hwnd;
        if (_windowHandle == nint.Zero)
        {
            throw new InvalidOperationException("SDL reported a null window handle.");
        }
    }

    private void RunLoop()
    {
        _isRunning = true;
        var evt = new Event();
        var clock = Stopwatch.StartNew();
        var lastTime = 0.0;

        while (_isRunning)
        {
            PumpEvents(ref evt);

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
        switch ((WindowEventID)evt.Window.Event)
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
        CreateTriangleVertexBuffer();
        CreatePipelineStateObjects();
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
            priority: (int)CommandQueuePriority.Normal,
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
            Width = (uint)Math.Max(_framebufferSize.X, 1),
            Height = (uint)Math.Max(_framebufferSize.Y, 1)
        };

        _factory = _dxgi.CreateDXGIFactory<IDXGIFactory2>();

        var factoryPtr = _factory.Handle;
        var queuePtr = (IUnknown*)_commandQueue.Handle;
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
        SilkMarshal.ThrowHResult(swapChain1->QueryInterface(ref swapChain3Guid, (void**)&swapChain3));
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
            SilkMarshal.ThrowHResult(_swapchain.GetBuffer((uint)i, out _renderTargets[i]));
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

    private void CreateTriangleVertexBuffer()
    {
        var vertexStride = Unsafe.SizeOf<Vector4>();
        var vertexBufferSize = (ulong)(_triangleVertices.Length * vertexStride);

        var defaultHeapProps = new HeapProperties(HeapType.Default);
        var resourceDesc = new ResourceDesc
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

        SilkMarshal.ThrowHResult(
            _device.CreateCommittedResource(
                &defaultHeapProps,
                HeapFlags.None,
                in resourceDesc,
                ResourceStates.CopyDest,
                null,
                out _vertexBuffer));

        var uploadHeapProps = new HeapProperties(HeapType.Upload);
        SilkMarshal.ThrowHResult(
            _device.CreateCommittedResource(
                &uploadHeapProps,
                HeapFlags.None,
                in resourceDesc,
                ResourceStates.GenericRead,
                null,
                out _vertexBufferUpload));

        void* mappedData = null;
        var uploadPtr = _vertexBufferUpload.Handle;
        SilkMarshal.ThrowHResult(uploadPtr->Map(0, null, &mappedData));
        try
        {
            fixed (Vector4* vertexData = _triangleVertices)
            {
                System.Buffer.MemoryCopy(vertexData, mappedData, (long)vertexBufferSize, (long)vertexBufferSize);
            }
        }
        finally
        {
            uploadPtr->Unmap(0, null);
        }

        SilkMarshal.ThrowHResult(_commandAllocators[0].Reset());
        SilkMarshal.ThrowHResult(_commandList.Reset(_commandAllocators[0].Handle, (ID3D12PipelineState*)null));
        _commandList.CopyBufferRegion(_vertexBuffer.Handle, 0, _vertexBufferUpload.Handle, 0, vertexBufferSize);

        var vertexBarrier = new ResourceBarrier { Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None };
        vertexBarrier.Anonymous.Transition = new()
        {
            PResource = _vertexBuffer.Handle,
            Subresource = D3D12.ResourceBarrierAllSubresources,
            StateBefore = ResourceStates.CopyDest,
            StateAfter = ResourceStates.VertexAndConstantBuffer
        };
        _commandList.ResourceBarrier(1, &vertexBarrier);

        SilkMarshal.ThrowHResult(_commandList.Close());
        ID3D12CommandList* lists = (ID3D12CommandList*)_commandList.Handle;
        _commandQueue.ExecuteCommandLists(1, &lists);

        SignalAndWait();

        var vertexBufferPtr = _vertexBuffer.Handle;
        _vertexBufferView = new VertexBufferView
        {
            BufferLocation = vertexBufferPtr->GetGPUVirtualAddress(),
            SizeInBytes = (uint)vertexBufferSize,
            StrideInBytes = (uint)vertexStride
        };

        if (_vertexBufferUpload.Handle is not null)
        {
            _vertexBufferUpload.Dispose();
            _vertexBufferUpload = default;
        }
    }

    private void CreatePipelineStateObjects()
    {
        var vertexShaderBytes = _shaderCompiler.GetDxil(TriangleShaderFile, "vertexShader", "vs_6_0");
        var pixelShaderBytes = _shaderCompiler.GetDxil(TriangleShaderFile, "fragmentShader", "ps_6_0");

        var rootSignatureDesc = new RootSignatureDesc
        {
            NumParameters = 0,
            PParameters = null,
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
        var serializeResult = _d3d12.SerializeVersionedRootSignature(&versionedDesc, &rootSignatureBlob, &rootSignatureError);
        if (rootSignatureError is not null)
        {
            var message = Marshal.PtrToStringAnsi((nint)rootSignatureError->GetBufferPointer());
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

        Span<byte> semanticName = [(byte)'P', (byte)'O', (byte)'S', (byte)'I', (byte)'T', (byte)'I', (byte)'O', (byte)'N', 0];
        InputElementDesc inputElement = new()
        {
            SemanticName = (byte*)Unsafe.AsPointer(ref semanticName.GetPinnableReference()),
            SemanticIndex = 0,
            Format = Format.FormatR32G32B32A32Float,
            InputSlot = 0,
            AlignedByteOffset = 0,
            InputSlotClass = InputClassification.PerVertexData,
            InstanceDataStepRate = 0
        };

        InputLayoutDesc inputLayout = new()
        {
            PInputElementDescs = &inputElement,
            NumElements = 1
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
            RenderTargetWriteMask = (byte)ColorWriteEnable.All
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
            DepthEnable = 0,
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
                BytecodeLength = (nuint)vertexShaderBytes.Length
            };

            var shaderBytecodePS = new ShaderBytecode
            {
                PShaderBytecode = pixelPtr,
                BytecodeLength = (nuint)pixelShaderBytes.Length
            };

            var psoDesc = new GraphicsPipelineStateDesc
            {
                PRootSignature = (ID3D12RootSignature*)_rootSignature.Handle,
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

            SilkMarshal.ThrowHResult(_device.CreateGraphicsPipelineState(in psoDesc, out _pipelineState));
        }
    }

    private void OnUpdate(double deltaSeconds)
    {
        // Reserved for future logic.
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

        SilkMarshal.ThrowHResult(_swapchain.ResizeBuffers(FrameCount, (uint)newSize.X, (uint)newSize.Y, Format.FormatB8G8R8A8Unorm, 0));
        _backbufferIndex = _swapchain.GetCurrentBackBufferIndex();

        CreateRtvHeapAndTargets();
    }

    private void OnRender(double deltaSeconds)
    {
        var frameIdx = _backbufferIndex;

        if (_fence.GetCompletedValue() < _frameFenceValues[frameIdx])
        {
            SilkMarshal.ThrowHResult(_fence.SetEventOnCompletion(_frameFenceValues[frameIdx], (void*)_fenceEvent));
            WaitForSingleObject(_fenceEvent, 0xFFFFFFFF);
        }

        SilkMarshal.ThrowHResult(_commandAllocators[frameIdx].Reset());
        SilkMarshal.ThrowHResult(_commandList.Reset(_commandAllocators[frameIdx].Handle, (ID3D12PipelineState*)_pipelineState.Handle));

        var barrierBegin = new ResourceBarrier { Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None };
        barrierBegin.Anonymous.Transition = new()
        {
            PResource = _renderTargets[frameIdx].Handle,
            Subresource = D3D12.ResourceBarrierAllSubresources,
            StateBefore = ResourceStates.Present,
            StateAfter = ResourceStates.RenderTarget
        };
        _commandList.ResourceBarrier(1, &barrierBegin);

        var fb = _framebufferSize;
        var vp = new Viewport { TopLeftX = 0, TopLeftY = 0, Width = fb.X, Height = fb.Y, MinDepth = 0.0f, MaxDepth = 1.0f };
        _commandList.RSSetViewports(1, &vp);
        var sc = new Box2D<int>(0, 0, fb.X, fb.Y);
        _commandList.RSSetScissorRects(1, &sc);

        var rtvHandle = _rtvCpuHandles[frameIdx];
        _commandList.OMSetRenderTargets(1, &rtvHandle, new(false), (CpuDescriptorHandle*)null);
        fixed (float* clear = _backgroundColour)
        {
            _commandList.ClearRenderTargetView(rtvHandle, clear, 0, (Box2D<int>*)null);
        }

        _commandList.SetPipelineState((ID3D12PipelineState*)_pipelineState.Handle);
        _commandList.SetGraphicsRootSignature(_rootSignature.Handle);
        _commandList.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        var vbView = _vertexBufferView;
        _commandList.IASetVertexBuffers(0, 1, &vbView);
        _commandList.DrawInstanced(3, 1, 0, 0);

        var barrierEnd = new ResourceBarrier { Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None };
        barrierEnd.Anonymous.Transition = new()
        {
            PResource = _renderTargets[frameIdx].Handle,
            Subresource = D3D12.ResourceBarrierAllSubresources,
            StateBefore = ResourceStates.RenderTarget,
            StateAfter = ResourceStates.Present
        };
        _commandList.ResourceBarrier(1, &barrierEnd);

        SilkMarshal.ThrowHResult(_commandList.Close());
        ID3D12CommandList* lists = (ID3D12CommandList*)_commandList.Handle;
        _commandQueue.ExecuteCommandLists(1, &lists);

        SilkMarshal.ThrowHResult(_swapchain.Present(1, 0));

        _fenceValue++;
        SilkMarshal.ThrowHResult(_commandQueue.Signal(_fence, _fenceValue));
        _frameFenceValues[frameIdx] = _fenceValue;

        _backbufferIndex = _swapchain.GetCurrentBackBufferIndex();
    }

    private void SignalAndWait()
    {
        _fenceValue++;
        SilkMarshal.ThrowHResult(_commandQueue.Signal(_fence, _fenceValue));
        if (_fence.GetCompletedValue() < _fenceValue)
        {
            SilkMarshal.ThrowHResult(_fence.SetEventOnCompletion(_fenceValue, (void*)_fenceEvent));
            WaitForSingleObject(_fenceEvent, 0xFFFFFFFF);
        }
    }

    private void WaitForGpu()
    {
        _fenceValue++;
        SilkMarshal.ThrowHResult(_commandQueue.Signal(_fence, _fenceValue));
        SilkMarshal.ThrowHResult(_fence.SetEventOnCompletion(_fenceValue, (void*)_fenceEvent));
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
        _factory.Dispose();
        _swapchain.Dispose();
        _commandQueue.Dispose();
        if (_pipelineState.Handle is not null)
        {
            _pipelineState.Dispose();
            _pipelineState = default;
        }
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
