using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
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

    private readonly int _width;
    private readonly int _height;
    private readonly IShaderCompiler _shaderCompiler;
    private readonly Sdl _sdl;

    private DXGI _dxgi = null!;
    private D3D12 _d3d12 = null!;
    private D3DCompiler _compiler = null!;

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
        _compiler = D3DCompiler.GetApi();

        CreateDeviceAndQueue();
        CreateSwapchain();
        CreateRtvHeapAndTargets();
        CreateCommandAllocatorsAndList();
        CreateSyncObjects();
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
        SilkMarshal.ThrowHResult(_commandList.Reset(_commandAllocators[frameIdx].Handle, (ID3D12PipelineState*)null));

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
        _device.Dispose();
        _compiler.Dispose();
        _d3d12.Dispose();
        _dxgi.Dispose();

        if (_fence.Handle is not null)
        {
            _fence.Dispose();
        }

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
