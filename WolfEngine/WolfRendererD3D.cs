using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace WolfEngine;

public unsafe class WolfRendererD3D
{
	private const int FrameCount = 2;

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
	private ComPtr<ID3D12Resource>[] _renderTargets = new ComPtr<ID3D12Resource>[FrameCount];
	private ComPtr<ID3D12CommandAllocator>[] _commandAllocators = new ComPtr<ID3D12CommandAllocator>[FrameCount];
	private ComPtr<ID3D12GraphicsCommandList> _commandList = default;
	private ComPtr<ID3D12Fence> _fence = default;
	private ulong _fenceValue = 0;
	private nint _fenceEvent = nint.Zero;

	private uint _backbufferIndex;

	private IWindow _window;

	private readonly float[] _backgroundColour = new[] {0.392f, 0.584f, 0.929f, 1.0f};
	
	public WolfRendererD3D(int width, int height)
	{
		var options = WindowOptions.Default;
		options.Size = new(width, height);
		options.Title = "Hello DirectX12";
		options.API = GraphicsAPI.None;
		_window = Window.Create(options);

		_window.Load += OnLoad;
		_window.Update += OnUpdate;
		_window.Render += OnRender;
		_window.FramebufferResize += OnFramebufferResize;

		_window.Run();

		Dispose();
	}

	private void OnLoad()
	{
		_dxgi = DXGI.GetApi(_window);
		_d3d12 = D3D12.GetApi();
		_compiler = D3DCompiler.GetApi();

		var input = _window.CreateInput();
		foreach (var keyboard in input.Keyboards)
		{
			keyboard.KeyDown += OnKeyDown;
		}

		CreateDeviceAndQueue();
		CreateSwapchain();
		CreateRtvHeapAndTargets();
		CreateCommandAllocatorsAndList();
		CreateSyncObjects();
	}

	private void CreateDeviceAndQueue()
	{
		// Create device (null adapter = default adapter)
		SilkMarshal.ThrowHResult
		(
			_d3d12.CreateDevice
			(
				_adapter,
				D3DFeatureLevel.Level120,
				out _device
			)
		);
		
		var commandQueueDescription = new CommandQueueDesc(
			type: CommandListType.Direct,
			priority: (int)CommandQueuePriority.Normal,
			flags: CommandQueueFlags.None);
		
		// Create command queue
		SilkMarshal.ThrowHResult
		(
			_device.CreateCommandQueue(commandQueueDescription, out _commandQueue)
		);
	}

	private void CreateSwapchain()
	{
		var swapChainDesc = new SwapChainDesc1
		{
			BufferCount = FrameCount,
			Format = Format.FormatB8G8R8A8Unorm,
			BufferUsage = DXGI.UsageRenderTargetOutput,
			SwapEffect = SwapEffect.FlipDiscard,
			SampleDesc = new SampleDesc(1, 0)
		};

		_factory = _dxgi.CreateDXGIFactory<IDXGIFactory2>();

		// IMPORTANT: CreateSwapChainForHwnd expects the *command queue*, not the device
		SilkMarshal.ThrowHResult
		(
			_factory.CreateSwapChainForHwnd
			(
				_commandQueue,
				_window.Native!.DXHandle!.Value,
				in swapChainDesc,
				null,
				ref Unsafe.NullRef<IDXGIOutput>(),
				ref _swapchain
			)
		);

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

		// Create render target views for each back buffer
		var rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
		for (var i = 0; i < FrameCount; i++)
		{
			SilkMarshal.ThrowHResult(_swapchain.GetBuffer((uint)i, out _renderTargets[i]));

			_device.CreateRenderTargetView(_renderTargets[i], null, rtvHandle);

			// Offset the handle for the next RTV
			rtvHandle.Ptr += (nuint)_rtvDescriptorSize;
		}
	}

	private void CreateCommandAllocatorsAndList()
	{
		for (var i = 0; i < FrameCount; i++)
		{
			SilkMarshal.ThrowHResult(_device.CreateCommandAllocator(CommandListType.Direct, out _commandAllocators[i]));
		}

		SilkMarshal.ThrowHResult(_device.CreateCommandList<ID3D12CommandAllocator, ID3D12PipelineState, ID3D12GraphicsCommandList>(0, CommandListType.Direct, _commandAllocators[0], default, out _commandList));
		// SilkMarshal.ThrowHResult(_device.CreateCommandList(0, CommandListType.Direct, _commandAllocators[0], default, out _commandList));
		// Command lists are created in the recording state; close until we start rendering
		SilkMarshal.ThrowHResult(_commandList.Close());
	}

	private void CreateSyncObjects()
	{
		SilkMarshal.ThrowHResult(_device.CreateFence(0, FenceFlags.None, out _fence));
		_fenceValue = 0;
		_fenceEvent = CreateEventEx(nint.Zero, null, 0, 0x1F0003 /* EVENT_ALL_ACCESS */);
		if (_fenceEvent == nint.Zero)
		{
			throw new InvalidOperationException("Failed to create fence event.");
		}
	}

	private void OnUpdate(double deltaSeconds) { /* No-op for now */ }

	private void OnFramebufferResize(Vector2D<int> newSize)
	{
		WaitForGpu();

		// Release current targets
		for (var i = 0; i < FrameCount; i++)
		{
			if (_renderTargets[i].Handle != null)
			{
				_renderTargets[i].Dispose();
			}
		}

		SilkMarshal.ThrowHResult(_swapchain.ResizeBuffers((uint)FrameCount, (uint)newSize.X, (uint)newSize.Y, Format.FormatB8G8R8A8Unorm, 0));
		_backbufferIndex = _swapchain.GetCurrentBackBufferIndex();

		CreateRtvHeapAndTargets();
	}

	private void OnRender(double deltaSeconds)
	{
		var frameIdx = _backbufferIndex;

		// Reset allocator & list
		SilkMarshal.ThrowHResult(_commandAllocators[frameIdx].Reset());
		SilkMarshal.ThrowHResult(_commandList.Reset((ID3D12CommandAllocator*)_commandAllocators[frameIdx].Handle, (ID3D12PipelineState*)null));
		// SilkMarshal.ThrowHResult(_commandList.Reset(_commandAllocators[frameIdx], default));

		// Transition back buffer: PRESENT -> RENDER_TARGET
		{
			var barrier = new ResourceBarrier
			{
				Type = ResourceBarrierType.Transition,
				Flags = ResourceBarrierFlags.None
			};
			barrier.Anonymous.Transition = new ResourceTransitionBarrier
			{
				PResource = _renderTargets[frameIdx],
				Subresource = D3D12.ResourceBarrierAllSubresources,
				StateBefore = ResourceStates.Present,
				StateAfter = ResourceStates.RenderTarget
			};
			_commandList.ResourceBarrier(1, &barrier);
		}

		// Get current RTV handle
		var rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
		rtvHandle.Ptr += (nuint)(_rtvDescriptorSize * frameIdx);

		// Clear render target
		// _commandList.OMSetRenderTargets(1, &rtvHandle, false, null);
		// _commandList.OMSetRenderTargets(1, &rtvHandle, new Silk.NET.Core.Bool32(false), null);
		_commandList.OMSetRenderTargets(1, &rtvHandle, new Silk.NET.Core.Bool32(false), (CpuDescriptorHandle*)null);
		
		fixed (float* clear = _backgroundColour)
		{
			_commandList.ClearRenderTargetView(rtvHandle, clear, 0, (Silk.NET.Maths.Box2D<int>*)null);
			// _commandList.ClearRenderTargetView(rtvHandle, clear, 0, null);
		}

		// Transition back buffer: RENDER_TARGET -> PRESENT
		{
			var barrier = new ResourceBarrier
			{
				Type = ResourceBarrierType.Transition,
				Flags = ResourceBarrierFlags.None
			};
			barrier.Anonymous.Transition = new ResourceTransitionBarrier
			{
				PResource = _renderTargets[frameIdx],
				Subresource = D3D12.ResourceBarrierAllSubresources,
				StateBefore = ResourceStates.RenderTarget,
				StateAfter = ResourceStates.Present
			};
			_commandList.ResourceBarrier(1, &barrier);
		}

		// Close & execute
		SilkMarshal.ThrowHResult(_commandList.Close());
		ID3D12CommandList* lists = (ID3D12CommandList*)_commandList.Handle;
		_commandQueue.ExecuteCommandLists(1, &lists);

		// Present
		SilkMarshal.ThrowHResult(_swapchain.Present(1, 0));

		// Simple CPU/GPU sync for now (wait every frame)
		SignalAndWait();

		// Get the new backbuffer index
		_backbufferIndex = _swapchain.GetCurrentBackBufferIndex();
	}

	private void SignalAndWait()
	{
		_fenceValue++;
		SilkMarshal.ThrowHResult(_commandQueue.Signal(_fence, _fenceValue));
		if (_fence.GetCompletedValue() < _fenceValue)
		{
			//SilkMarshal.ThrowHResult(_fence.SetEventOnCompletion(_fenceValue, _fenceEvent));
			SilkMarshal.ThrowHResult(_fence.SetEventOnCompletion(_fenceValue, (void*)_fenceEvent));
			WaitForSingleObject(_fenceEvent, 0xFFFFFFFF);
		}
	}

	private void WaitForGpu()
	{
		_fenceValue++;
		SilkMarshal.ThrowHResult(_commandQueue.Signal(_fence, _fenceValue));
		// SilkMarshal.ThrowHResult(_fence.SetEventOnCompletion(_fenceValue, _fenceEvent));
		SilkMarshal.ThrowHResult(_fence.SetEventOnCompletion(_fenceValue, (void*)_fenceEvent));
		WaitForSingleObject(_fenceEvent, 0xFFFFFFFF);
	}

	private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
	{
		if (key == Key.Escape) _window.Close();
	}

	private void Dispose()
	{
		WaitForGpu();

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

		if (_fence.Handle != null)
		{
			_fence.Dispose();
		}
		if (_fenceEvent != nint.Zero)
		{
			CloseHandle(_fenceEvent);
			_fenceEvent = nint.Zero;
		}

		_window.Dispose();
	}

	#region Win32 sync interop
	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern nint CreateEventEx(nint lpEventAttributes, string? lpName, uint dwFlags, uint dwDesiredAccess);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(nint hObject);
	#endregion
}
