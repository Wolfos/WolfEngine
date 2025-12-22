using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ImGuiNET;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using SharpMetal.QuartzCore;
using Silk.NET.Core.Native;
using Silk.NET.SDL;
using WolfEngine.Backend.Metal;
using WolfEngine.ECS;
using WolfEngine.Mathematics;
using WolfEngine.Platform;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Backend.Metal;
using WolfEngine.Rendering.UI;
using WolfEngine.Input;
using AbstractionBlendMode = WolfEngine.Rendering.Abstraction.BlendMode;

namespace WolfEngine;

[SupportedOSPlatform("macos")]
public unsafe class WolfRendererMetal : IRenderer
{
    private const string WindowTitle = "WolfEngine";
    private static int _textureUploadReadbacks;

    private readonly int _width;
    private readonly int _height;
    private readonly IShaderCompiler _shaderCompiler;
    private readonly IArenaAllocator _renderCommandAllocator;
    private readonly IInputSystem _inputSystem;
    private readonly IImGuiInputSink _imguiInputSink;
    private readonly MetalImGuiRenderer _imguiRenderer;
    private readonly ConcurrentQueue<RenderCommand> _pendingCommands = new();
    private readonly Dictionary<Mesh, MeshResources> _meshResources = new();
    private readonly List<DrawInstruction> _drawCommands = new();
    private Camera _camera;
    private Transform _cameraTransform;
    private bool _hasCamera;
    private MTLTexture _depthTexture;
    private MTLDepthStencilState _depthState;
    private readonly MTLClearColor _clearColor = new() { red = 0.392, green = 0.584, blue = 0.929, alpha = 1.0 };
    private readonly Sdl _sdl;
    private bool _hasMousePosition;

    private MTLDevice _device;
    private MTLCommandQueue _commandQueue;
    private MetalDevice _gfxDevice;
    private Window* _window;
    private void* _metalView;
    private CAMetalLayer _metalLayer;
    private CAMetalDrawable _currentDrawable;
    private bool _isRunning;
    private bool _hasDrawableSize;
    private double _drawableWidth;
    private double _drawableHeight;
    private DescriptorHandle _linearSamplerHandle = DescriptorHandle.Invalid;
    private DescriptorHandle _skyboxSamplerHandle = DescriptorHandle.Invalid;
    private Action _startupCallback = static () => { };
    private Action<float> _updateCallback = static deltaTime => { };
    private Action<float> _renderCallback = static deltaTime => { };
    private bool _useRenderGraph;
    private IGfxPipeline _iblIrradiancePipeline;
    private IGfxPipeline _iblPrefilterPipeline;
    private IGfxPipeline _iblBrdfLutPipeline;


    private static readonly Selector NextDrawableSelector = new("nextDrawable");
    private static readonly Selector DrawableSizeSelector = new("setDrawableSize:");

    private sealed class MeshResources
    {
        public MeshResources(MTLBuffer vertexBuffer, MTLBuffer indexBuffer, ulong indexCount)
        {
            VertexBuffer = vertexBuffer;
            IndexBuffer = indexBuffer;
            IndexCount = indexCount;
        }

        public MTLBuffer VertexBuffer { get; }

        public MTLBuffer IndexBuffer { get; }

        public ulong IndexCount { get; }
    }
    
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VertexData
    {
        public Vector4 Position;
        public Vector3 Normal;
        public Vector2 UV;
        public Vector4 Tangent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CameraParams
    {
        public Matrix4x4 ViewProjection;
        public Vector4 CameraPosition;
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

    public WolfRendererMetal(IShaderCompiler shaderCompiler, IArenaAllocator renderCommandAllocator, IInputSystem inputSystem, ImGuiUiSystem imguiSystem)
    {
        if (OperatingSystem.IsMacOS() == false)
        {
            throw new PlatformNotSupportedException("Metal renderer is only supported on macOS.");
        }

        _width = 1280;
        _height = 720;
        _shaderCompiler = shaderCompiler;
        _renderCommandAllocator = renderCommandAllocator ?? throw new ArgumentNullException(nameof(renderCommandAllocator));
        _inputSystem = inputSystem ?? throw new ArgumentNullException(nameof(inputSystem));
        _imguiInputSink = imguiSystem ?? throw new ArgumentNullException(nameof(imguiSystem));
        _imguiRenderer = new MetalImGuiRenderer(shaderCompiler);

        ObjectiveC.LinkMetal();
        ObjectiveC.LinkCoreGraphics();
        ObjectiveC.LinkAppKit();
        ObjectiveC.LinkMetalKit();

        _sdl = Sdl.GetApi();
    }

    public void SubmitCommand(RenderCommand command)
    {
        _pendingCommands.Enqueue(command);
    }

    public void Run(Action startup, Action<float> update, Action<float> render)
    {
        _startupCallback = startup ?? throw new ArgumentNullException(nameof(startup));
        _updateCallback = update ?? throw new ArgumentNullException(nameof(update));
        _renderCallback = render ?? throw new ArgumentNullException(nameof(render));
        _useRenderGraph = true;

        try
        {
            CreateDevice();
            CreateCommandQueue();
            CreateDepthState();
            InitializeWindow();
            MainLoop();
        }
        finally
        {
            Shutdown();
        }
    }

    public void Run(Action startup, Action<float> update)
    {
        Run(startup, update, static _ => { });
    }

    private void MainLoop()
    {
        _isRunning = true;
        var @event = new Event();

        _startupCallback();

        while (_isRunning)
        {
            PumpEvents(ref @event);

            // TODO: Delta time
            _updateCallback(0);
            _renderCallback(0);
            if (_useRenderGraph == false)
            {
                ProcessPendingCommands();
                var rendered = RenderFrame();

                if (rendered == false)
                {
                    _sdl.Delay(1);
                }
            }
        }
    }

    private void PumpEvents(ref Event @event)
    {
        while (_sdl.PollEvent(ref @event) != 0)
        {
            switch ((EventType)@event.Type)
            {
                case EventType.Quit:
                    _isRunning = false;
                    break;
                case EventType.Windowevent:
                    HandleWindowEvent(@event);
                    break;
                case EventType.Keydown:
                    HandleKeyDown(@event.Key);
                    break;
                case EventType.Keyup:
                    HandleKeyUp(@event.Key);
                    break;
                case EventType.Textinput:
                    HandleTextInput(@event.Text);
                    break;
                case EventType.Mousemotion:
                    HandleMouseMotion(@event.Motion);
                    break;
                case EventType.Mousebuttondown:
                    HandleMouseButton(@event.Button, true);
                    break;
                case EventType.Mousebuttonup:
                    HandleMouseButton(@event.Button, false);
                    break;
                case EventType.Mousewheel:
                    HandleMouseWheel(@event.Wheel);
                    break;
            }
        }
    }

    private void HandleKeyDown(KeyboardEvent keyEvent)
    {
        var scancode = keyEvent.Keysym.Scancode;
        if (TryMapKey(scancode, out var binding))
        {
            _inputSystem.SetButton(binding, true);
        }
        if (TryConvertKey(scancode, out var imguiKey))
        {
            _imguiInputSink.SetKey(imguiKey, true);
        }

        if (scancode == Scancode.ScancodeEscape)
        {
            _isRunning = false;
        }
    }

    private void HandleKeyUp(KeyboardEvent keyEvent)
    {
        var scancode = keyEvent.Keysym.Scancode;
        if (TryMapKey(scancode, out var binding))
        {
            _inputSystem.SetButton(binding, false);
        }
        if (TryConvertKey(scancode, out var imguiKey))
        {
            _imguiInputSink.SetKey(imguiKey, false);
        }
    }

    private void HandleTextInput(TextInputEvent textEvent)
    {
        const int textSize = 32;
        unsafe
        {
            var textPtr = (byte*)Unsafe.AsPointer(ref textEvent.Text[0]);
            var span = new ReadOnlySpan<byte>(textPtr, textSize);
            var terminator = span.IndexOf((byte)0);
            if (terminator >= 0)
            {
                span = span[..terminator];
            }

            if (span.IsEmpty)
            {
                return;
            }

            var text = Encoding.UTF8.GetString(span);
            for (var i = 0; i < text.Length; i++)
            {
                _imguiInputSink.AddChar(text[i]);
            }
        }
    }

    private void HandleMouseMotion(MouseMotionEvent motionEvent)
    {
        var position = new Vector2(motionEvent.X, motionEvent.Y);
        _inputSystem.SetAxis2D(InputActionBinding.MousePosition, position);
        _imguiInputSink.SetMousePosition(position);

        var delta = new Vector2(motionEvent.Xrel, motionEvent.Yrel);
        if (_hasMousePosition || delta != Vector2.Zero)
        {
            _inputSystem.SetAxis2D(InputActionBinding.MouseDelta, delta);
        }

        _hasMousePosition = true;
    }

    private void HandleMouseButton(MouseButtonEvent buttonEvent, bool isDown)
    {
        if (TryMapMouseButton(buttonEvent.Button, out var binding))
        {
            _inputSystem.SetButton(binding, isDown);
        }

        var imguiIndex = buttonEvent.Button switch
        {
            Sdl.ButtonLeft => 0,
            Sdl.ButtonRight => 1,
            Sdl.ButtonMiddle => 2,
            Sdl.ButtonX1 => 3,
            Sdl.ButtonX2 => 4,
            _ => -1
        };
        if (imguiIndex >= 0)
        {
            _imguiInputSink.SetMouseButton(imguiIndex, isDown);
        }
    }

    private void HandleMouseWheel(MouseWheelEvent wheelEvent)
    {
        var scroll = new Vector2(wheelEvent.PreciseX, wheelEvent.PreciseY);
        if (scroll == Vector2.Zero)
        {
            scroll = new Vector2(wheelEvent.X, wheelEvent.Y);
        }

        var direction = (MouseWheelDirection)wheelEvent.Direction;
        if (direction == MouseWheelDirection.Flipped)
        {
            scroll = -scroll;
        }

        _inputSystem.SetAxis2D(InputActionBinding.MouseScroll, scroll);
        _imguiInputSink.AddMouseScroll(scroll);
    }

    private static bool TryMapMouseButton(byte button, out InputActionBinding binding)
    {
        binding = button switch
        {
            Sdl.ButtonLeft => InputActionBinding.MouseButtonLeft,
            Sdl.ButtonRight => InputActionBinding.MouseButtonRight,
            Sdl.ButtonMiddle => InputActionBinding.MouseButtonMiddle,
            Sdl.ButtonX1 => InputActionBinding.MouseButton4,
            Sdl.ButtonX2 => InputActionBinding.MouseButton5,
            _ => InputActionBinding.None
        };

        return binding != InputActionBinding.None;
    }

    private static bool TryMapKey(Scancode scancode, out InputActionBinding binding)
    {
        binding = scancode switch
        {
            Scancode.ScancodeA => InputActionBinding.KeyA,
            Scancode.ScancodeB => InputActionBinding.KeyB,
            Scancode.ScancodeC => InputActionBinding.KeyC,
            Scancode.ScancodeD => InputActionBinding.KeyD,
            Scancode.ScancodeE => InputActionBinding.KeyE,
            Scancode.ScancodeF => InputActionBinding.KeyF,
            Scancode.ScancodeG => InputActionBinding.KeyG,
            Scancode.ScancodeH => InputActionBinding.KeyH,
            Scancode.ScancodeI => InputActionBinding.KeyI,
            Scancode.ScancodeJ => InputActionBinding.KeyJ,
            Scancode.ScancodeK => InputActionBinding.KeyK,
            Scancode.ScancodeL => InputActionBinding.KeyL,
            Scancode.ScancodeM => InputActionBinding.KeyM,
            Scancode.ScancodeN => InputActionBinding.KeyN,
            Scancode.ScancodeO => InputActionBinding.KeyO,
            Scancode.ScancodeP => InputActionBinding.KeyP,
            Scancode.ScancodeQ => InputActionBinding.KeyQ,
            Scancode.ScancodeR => InputActionBinding.KeyR,
            Scancode.ScancodeS => InputActionBinding.KeyS,
            Scancode.ScancodeT => InputActionBinding.KeyT,
            Scancode.ScancodeU => InputActionBinding.KeyU,
            Scancode.ScancodeV => InputActionBinding.KeyV,
            Scancode.ScancodeW => InputActionBinding.KeyW,
            Scancode.ScancodeX => InputActionBinding.KeyX,
            Scancode.ScancodeY => InputActionBinding.KeyY,
            Scancode.ScancodeZ => InputActionBinding.KeyZ,
            Scancode.Scancode0 => InputActionBinding.Key0,
            Scancode.Scancode1 => InputActionBinding.Key1,
            Scancode.Scancode2 => InputActionBinding.Key2,
            Scancode.Scancode3 => InputActionBinding.Key3,
            Scancode.Scancode4 => InputActionBinding.Key4,
            Scancode.Scancode5 => InputActionBinding.Key5,
            Scancode.Scancode6 => InputActionBinding.Key6,
            Scancode.Scancode7 => InputActionBinding.Key7,
            Scancode.Scancode8 => InputActionBinding.Key8,
            Scancode.Scancode9 => InputActionBinding.Key9,
            Scancode.ScancodeF1 => InputActionBinding.KeyF1,
            Scancode.ScancodeF2 => InputActionBinding.KeyF2,
            Scancode.ScancodeF3 => InputActionBinding.KeyF3,
            Scancode.ScancodeF4 => InputActionBinding.KeyF4,
            Scancode.ScancodeF5 => InputActionBinding.KeyF5,
            Scancode.ScancodeF6 => InputActionBinding.KeyF6,
            Scancode.ScancodeF7 => InputActionBinding.KeyF7,
            Scancode.ScancodeF8 => InputActionBinding.KeyF8,
            Scancode.ScancodeF9 => InputActionBinding.KeyF9,
            Scancode.ScancodeF10 => InputActionBinding.KeyF10,
            Scancode.ScancodeF11 => InputActionBinding.KeyF11,
            Scancode.ScancodeF12 => InputActionBinding.KeyF12,
            Scancode.ScancodeEscape => InputActionBinding.KeyEscape,
            Scancode.ScancodeTab => InputActionBinding.KeyTab,
            Scancode.ScancodeCapslock => InputActionBinding.KeyCapsLock,
            Scancode.ScancodeLshift => InputActionBinding.KeyLeftShift,
            Scancode.ScancodeRshift => InputActionBinding.KeyRightShift,
            Scancode.ScancodeLctrl => InputActionBinding.KeyLeftControl,
            Scancode.ScancodeRctrl => InputActionBinding.KeyRightControl,
            Scancode.ScancodeLalt => InputActionBinding.KeyLeftAlt,
            Scancode.ScancodeRalt => InputActionBinding.KeyRightAlt,
            Scancode.ScancodeLgui => InputActionBinding.KeyLeftSuper,
            Scancode.ScancodeRgui => InputActionBinding.KeyRightSuper,
            Scancode.ScancodeMenu => InputActionBinding.KeyMenu,
            Scancode.ScancodeSpace => InputActionBinding.KeySpace,
            Scancode.ScancodeReturn => InputActionBinding.KeyEnter,
            Scancode.ScancodeBackspace => InputActionBinding.KeyBackspace,
            Scancode.ScancodeInsert => InputActionBinding.KeyInsert,
            Scancode.ScancodeDelete => InputActionBinding.KeyDelete,
            Scancode.ScancodeHome => InputActionBinding.KeyHome,
            Scancode.ScancodeEnd => InputActionBinding.KeyEnd,
            Scancode.ScancodePageup => InputActionBinding.KeyPageUp,
            Scancode.ScancodePagedown => InputActionBinding.KeyPageDown,
            Scancode.ScancodeUp => InputActionBinding.KeyArrowUp,
            Scancode.ScancodeDown => InputActionBinding.KeyArrowDown,
            Scancode.ScancodeLeft => InputActionBinding.KeyArrowLeft,
            Scancode.ScancodeRight => InputActionBinding.KeyArrowRight,
            Scancode.ScancodeMinus => InputActionBinding.KeyMinus,
            Scancode.ScancodeEquals => InputActionBinding.KeyEquals,
            Scancode.ScancodeLeftbracket => InputActionBinding.KeyLeftBracket,
            Scancode.ScancodeRightbracket => InputActionBinding.KeyRightBracket,
            Scancode.ScancodeBackslash => InputActionBinding.KeyBackslash,
            Scancode.ScancodeSemicolon => InputActionBinding.KeySemicolon,
            Scancode.ScancodeApostrophe => InputActionBinding.KeyApostrophe,
            Scancode.ScancodeGrave => InputActionBinding.KeyGrave,
            Scancode.ScancodeComma => InputActionBinding.KeyComma,
            Scancode.ScancodePeriod => InputActionBinding.KeyPeriod,
            Scancode.ScancodeSlash => InputActionBinding.KeySlash,
            Scancode.ScancodePrintscreen => InputActionBinding.KeyPrintScreen,
            Scancode.ScancodeScrolllock => InputActionBinding.KeyScrollLock,
            Scancode.ScancodePause => InputActionBinding.KeyPause,
            Scancode.ScancodeNumlockclear => InputActionBinding.KeyNumLock,
            Scancode.ScancodeKP0 => InputActionBinding.KeyNumpad0,
            Scancode.ScancodeKP1 => InputActionBinding.KeyNumpad1,
            Scancode.ScancodeKP2 => InputActionBinding.KeyNumpad2,
            Scancode.ScancodeKP3 => InputActionBinding.KeyNumpad3,
            Scancode.ScancodeKP4 => InputActionBinding.KeyNumpad4,
            Scancode.ScancodeKP5 => InputActionBinding.KeyNumpad5,
            Scancode.ScancodeKP6 => InputActionBinding.KeyNumpad6,
            Scancode.ScancodeKP7 => InputActionBinding.KeyNumpad7,
            Scancode.ScancodeKP8 => InputActionBinding.KeyNumpad8,
            Scancode.ScancodeKP9 => InputActionBinding.KeyNumpad9,
            Scancode.ScancodeKPDivide => InputActionBinding.KeyNumpadDivide,
            Scancode.ScancodeKPMultiply => InputActionBinding.KeyNumpadMultiply,
            Scancode.ScancodeKPMinus => InputActionBinding.KeyNumpadSubtract,
            Scancode.ScancodeKPPlus => InputActionBinding.KeyNumpadAdd,
            Scancode.ScancodeKPPeriod => InputActionBinding.KeyNumpadDecimal,
            Scancode.ScancodeKPEnter => InputActionBinding.KeyNumpadEnter,
            _ => InputActionBinding.None
        };

        return binding != InputActionBinding.None;
    }

    private static bool TryConvertKey(Scancode scancode, out ImGuiKey imguiKey)
    {
        imguiKey = scancode switch
        {
            Scancode.ScancodeTab => ImGuiKey.Tab,
            Scancode.ScancodeLshift => ImGuiKey.LeftShift,
            Scancode.ScancodeRshift => ImGuiKey.RightShift,
            Scancode.ScancodeLctrl => ImGuiKey.LeftCtrl,
            Scancode.ScancodeRctrl => ImGuiKey.RightCtrl,
            Scancode.ScancodeLalt => ImGuiKey.LeftAlt,
            Scancode.ScancodeRalt => ImGuiKey.RightAlt,
            Scancode.ScancodeLgui => ImGuiKey.LeftSuper,
            Scancode.ScancodeRgui => ImGuiKey.RightSuper,
            Scancode.ScancodeMenu => ImGuiKey.Menu,
            Scancode.ScancodeUp => ImGuiKey.UpArrow,
            Scancode.ScancodeDown => ImGuiKey.DownArrow,
            Scancode.ScancodeLeft => ImGuiKey.LeftArrow,
            Scancode.ScancodeRight => ImGuiKey.RightArrow,
            Scancode.ScancodeEscape => ImGuiKey.Escape,
            Scancode.ScancodeReturn => ImGuiKey.Enter,
            Scancode.ScancodeSpace => ImGuiKey.Space,
            Scancode.ScancodeBackspace => ImGuiKey.Backspace,
            Scancode.ScancodeInsert => ImGuiKey.Insert,
            Scancode.ScancodeDelete => ImGuiKey.Delete,
            Scancode.ScancodeHome => ImGuiKey.Home,
            Scancode.ScancodeEnd => ImGuiKey.End,
            Scancode.ScancodePageup => ImGuiKey.PageUp,
            Scancode.ScancodePagedown => ImGuiKey.PageDown,
            Scancode.ScancodeA => ImGuiKey.A,
            Scancode.ScancodeC => ImGuiKey.C,
            Scancode.ScancodeV => ImGuiKey.V,
            Scancode.ScancodeX => ImGuiKey.X,
            Scancode.ScancodeY => ImGuiKey.Y,
            Scancode.ScancodeZ => ImGuiKey.Z,
            Scancode.Scancode0 => ImGuiKey._0,
            Scancode.Scancode1 => ImGuiKey._1,
            Scancode.Scancode2 => ImGuiKey._2,
            Scancode.Scancode3 => ImGuiKey._3,
            Scancode.Scancode4 => ImGuiKey._4,
            Scancode.Scancode5 => ImGuiKey._5,
            Scancode.Scancode6 => ImGuiKey._6,
            Scancode.Scancode7 => ImGuiKey._7,
            Scancode.Scancode8 => ImGuiKey._8,
            Scancode.Scancode9 => ImGuiKey._9,
            Scancode.ScancodeF1 => ImGuiKey.F1,
            Scancode.ScancodeF2 => ImGuiKey.F2,
            Scancode.ScancodeF3 => ImGuiKey.F3,
            Scancode.ScancodeF4 => ImGuiKey.F4,
            Scancode.ScancodeF5 => ImGuiKey.F5,
            Scancode.ScancodeF6 => ImGuiKey.F6,
            Scancode.ScancodeF7 => ImGuiKey.F7,
            Scancode.ScancodeF8 => ImGuiKey.F8,
            Scancode.ScancodeF9 => ImGuiKey.F9,
            Scancode.ScancodeF10 => ImGuiKey.F10,
            Scancode.ScancodeF11 => ImGuiKey.F11,
            Scancode.ScancodeF12 => ImGuiKey.F12,
            Scancode.ScancodeGrave => ImGuiKey.GraveAccent,
            Scancode.ScancodeMinus => ImGuiKey.Minus,
            Scancode.ScancodeEquals => ImGuiKey.Equal,
            Scancode.ScancodeLeftbracket => ImGuiKey.LeftBracket,
            Scancode.ScancodeRightbracket => ImGuiKey.RightBracket,
            Scancode.ScancodeSemicolon => ImGuiKey.Semicolon,
            Scancode.ScancodeApostrophe => ImGuiKey.Apostrophe,
            Scancode.ScancodeBackslash => ImGuiKey.Backslash,
            Scancode.ScancodeComma => ImGuiKey.Comma,
            Scancode.ScancodePeriod => ImGuiKey.Period,
            Scancode.ScancodeSlash => ImGuiKey.Slash,
            Scancode.ScancodeKP0 => ImGuiKey.Keypad0,
            Scancode.ScancodeKP1 => ImGuiKey.Keypad1,
            Scancode.ScancodeKP2 => ImGuiKey.Keypad2,
            Scancode.ScancodeKP3 => ImGuiKey.Keypad3,
            Scancode.ScancodeKP4 => ImGuiKey.Keypad4,
            Scancode.ScancodeKP5 => ImGuiKey.Keypad5,
            Scancode.ScancodeKP6 => ImGuiKey.Keypad6,
            Scancode.ScancodeKP7 => ImGuiKey.Keypad7,
            Scancode.ScancodeKP8 => ImGuiKey.Keypad8,
            Scancode.ScancodeKP9 => ImGuiKey.Keypad9,
            Scancode.ScancodeKPPeriod => ImGuiKey.KeypadDecimal,
            Scancode.ScancodeKPDivide => ImGuiKey.KeypadDivide,
            Scancode.ScancodeKPMultiply => ImGuiKey.KeypadMultiply,
            Scancode.ScancodeKPMinus => ImGuiKey.KeypadSubtract,
            Scancode.ScancodeKPPlus => ImGuiKey.KeypadAdd,
            Scancode.ScancodeKPEnter => ImGuiKey.KeypadEnter,
            _ => ImGuiKey.None
        };

        return imguiKey != ImGuiKey.None;
    }

    private void HandleWindowEvent(Event @event)
    {
        if (@event.Window.Event == (byte)WindowEventID.Close)
        {
            _isRunning = false;
            return;
        }

        if (@event.Window.Event is (byte)WindowEventID.Resized or (byte)WindowEventID.SizeChanged)
        {
            UpdateDrawableSize();
        }
    }

    private void InitializeWindow()
    {
        if (_sdl.Init(Sdl.InitVideo) < 0)
        {
            throw new InvalidOperationException("Failed to initialise SDL video subsystem.");
        }

        var titlePtr = SilkMarshal.StringToPtr(WindowTitle, NativeStringEncoding.UTF8);
        try
        {
            var flags = WindowFlags.Resizable | WindowFlags.AllowHighdpi | WindowFlags.Metal;
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

        _metalView = _sdl.MetalCreateView(_window);
        if (_metalView is null)
        {
            throw new InvalidOperationException("Failed to create Metal view for SDL window.");
        }

        var layerPtr = _sdl.MetalGetLayer(_metalView);
        if (layerPtr is null)
        {
            throw new InvalidOperationException("Failed to retrieve CAMetalLayer from SDL view.");
        }

        _metalLayer = new CAMetalLayer(new IntPtr(layerPtr));
        _metalLayer.Device = _device;
        _metalLayer.PixelFormat = MTLPixelFormat.BGRA8Unorm;
        _metalLayer.FramebufferOnly = false;
        _metalLayer.DisplaySyncEnabled = true;

        UpdateDrawableSize();
    }

    private void CreateDevice()
    {
        _device = MTLDevice.CreateSystemDefaultDevice();
        if (_device.NativePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create the default Metal device.");
        }

        _gfxDevice = new MetalDevice(_device);
    }

    private void CreateCommandQueue()
    {
        _commandQueue = _device.NewCommandQueue();
        if (_commandQueue.NativePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create a Metal command queue.");
        }
    }

    private void CreateDepthState()
    {
        var descriptor = new MTLDepthStencilDescriptor();
        descriptor.DepthCompareFunction = MTLCompareFunction.Less;
        descriptor.DepthWriteEnabled = true;

        _depthState = _device.NewDepthStencilState(descriptor);
        descriptor.Dispose();
        if (_depthState.NativePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create depth-stencil state.");
        }
    }

    private void UpdateDrawableSize()
    {
        if (_window is null)
        {
            return;
        }

        int drawableWidth = 0;
        int drawableHeight = 0;
        _sdl.MetalGetDrawableSize(_window, ref drawableWidth, ref drawableHeight);

        if (drawableWidth <= 0 || drawableHeight <= 0)
        {
            return;
        }

        if (_hasDrawableSize && NearlyEqual(drawableWidth, _drawableWidth) && NearlyEqual(drawableHeight, _drawableHeight))
        {
            return;
        }

        _drawableWidth = drawableWidth;
        _drawableHeight = drawableHeight;
        _hasDrawableSize = true;

        var size = new NSPoint(drawableWidth, drawableHeight);
        ObjCNative.ObjcMsgSendDrawableSize(_metalLayer.NativePtr, DrawableSizeSelector.SelPtr, size);

        if (_gfxDevice is ITexturePoolDevice poolDevice)
        {
            poolDevice.ClearTexturePool();
        }

        CreateDepthTexture(drawableWidth, drawableHeight);
    }

    private void CreateDepthTexture(int width, int height)
    {
        if (_device.NativePtr == IntPtr.Zero || width <= 0 || height <= 0)
        {
            return;
        }

        if (_depthTexture.NativePtr != IntPtr.Zero)
        {
            _depthTexture.Dispose();
            _depthTexture = default;
        }

        var descriptor = new MTLTextureDescriptor();
        descriptor.TextureType = MTLTextureType.Type2D;
        descriptor.PixelFormat = MTLPixelFormat.Depth32Float;
        descriptor.Width = (ulong)width;
        descriptor.Height = (ulong)height;
        descriptor.MipmapLevelCount = 1;
        descriptor.SampleCount = 1;
        descriptor.StorageMode = MTLStorageMode.Private;
        descriptor.Usage = MTLTextureUsage.RenderTarget;

        _depthTexture = _device.NewTexture(descriptor);
        descriptor.Dispose();
        if (_depthTexture.NativePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create depth texture.");
        }
    }

    private bool RenderFrame()
    {
        if (_commandQueue.NativePtr == IntPtr.Zero)
        {
            return false;
        }

        if (_drawCommands.Count == 0)
        {
            _renderCommandAllocator.Reset();
            return false;
        }

        if (_hasCamera == false)
        {
            _drawCommands.Clear();
            _renderCommandAllocator.Reset();
            return false;
        }

        if (TryGetCameraMatrices(out var viewProjection, out var cameraPosition) == false)
        {
            _renderCommandAllocator.Reset();
            return false;
        }

        UpdateDrawableSize();

        var drawablePtr = ObjectiveC.IntPtr_objc_msgSend(_metalLayer.NativePtr, NextDrawableSelector);
        if (drawablePtr == IntPtr.Zero)
        {
            return false;
        }

        if (_depthTexture.NativePtr == IntPtr.Zero)
        {
            return false;
        }

        using var renderPassDescriptor = new MTLRenderPassDescriptor();
        var drawable = new CAMetalDrawable(drawablePtr);

        var colorAttachment = renderPassDescriptor.ColorAttachments.Object(0);
        colorAttachment.Texture = drawable.Texture;
        colorAttachment.LoadAction = MTLLoadAction.Clear;
        colorAttachment.StoreAction = MTLStoreAction.Store;
        colorAttachment.ClearColor = _clearColor;
        renderPassDescriptor.ColorAttachments.SetObject(colorAttachment, 0);

        var depthAttachment = renderPassDescriptor.DepthAttachment;
        depthAttachment.Texture = _depthTexture;
        depthAttachment.LoadAction = MTLLoadAction.Clear;
        depthAttachment.StoreAction = MTLStoreAction.DontCare;
        depthAttachment.ClearDepth = 1.0;

        var commandBuffer = _commandQueue.CommandBuffer();
        var encoder = commandBuffer.RenderCommandEncoder(renderPassDescriptor);

        if (_hasDrawableSize)
        {
            var viewport = new MTLViewport
            {
                originX = 0,
                originY = 0,
                width = _drawableWidth,
                height = _drawableHeight,
                znear = 0,
                zfar = 1
            };
            encoder.SetViewport(viewport);
        }

        if (_depthState.NativePtr != IntPtr.Zero)
        {
            encoder.SetDepthStencilState(_depthState);
        }
        encoder.SetCullMode(MTLCullMode.Back);
        encoder.SetFrontFacingWinding(MTLWinding.Clockwise);

        foreach (var drawCommand in _drawCommands)
        {
//             var mesh = drawCommand.mesh
//             var materialResources = drawCommand.Material.Resources as MtlMaterialResources;
//
//             encoder.SetRenderPipelineState(materialResources.PipelineState);
//             encoder.SetVertexBuffer(meshResources.VertexBuffer, 0, 0);
// #pragma warning disable CA2014
//             var transformCopy = drawCommand.Transform;
//             var transformPtr = stackalloc Matrix4x4[1];
//             transformPtr[0] = transformCopy;
//             var matrixSize = (ulong)sizeof(Matrix4x4);
//             encoder.SetVertexBytes((IntPtr)transformPtr, matrixSize, 1);
//
//             var cameraParamsPtr = stackalloc CameraParams[1];
//             cameraParamsPtr[0] = new CameraParams
//             {
//                 ViewProjection = viewProjection,
//                 CameraPosition = new Vector4(cameraPosition, 1.0f)
//             };
//             var cameraParamsSize = (ulong)sizeof(CameraParams);
//             encoder.SetVertexBytes((IntPtr)cameraParamsPtr, cameraParamsSize, 2);
//             encoder.SetFragmentBytes((IntPtr)cameraParamsPtr, cameraParamsSize, 2);
// #pragma warning restore CA2014
//             encoder.SetFragmentBuffer(materialResources.ColorBuffer, 0, 0);
//             encoder.DrawIndexedPrimitives(MTLPrimitiveType.Triangle, meshResources.IndexCount, MTLIndexType.UInt32, meshResources.IndexBuffer, 0);
        }

        encoder.EndEncoding();
        commandBuffer.PresentDrawable(drawable);
        commandBuffer.Commit();

        _drawCommands.Clear();
        _renderCommandAllocator.Reset();
        return true;
    }

    private void Shutdown()
    {
        if (_depthTexture.NativePtr != IntPtr.Zero)
        {
            _depthTexture.Dispose();
            _depthTexture = default;
        }

        if (_depthState.NativePtr != IntPtr.Zero)
        {
            _depthState.Dispose();
            _depthState = default;
        }

        if (_metalView is not null)
        {
            _sdl.MetalDestroyView(_metalView);
            _metalView = null;
        }

        if (_window is not null)
        {
            _sdl.DestroyWindow(_window);
            _window = null;
        }

        _sdl.Quit();
    }

    private MTLLibrary CreateShaderLibrary(Material material)
    {
        var libraryError = new NSError(IntPtr.Zero);
        var shaderSource = _shaderCompiler.GetMetalSource(material.ShaderPath);
        var library = _device.NewLibrary(NSStringHelper.From(shaderSource), new(IntPtr.Zero), ref libraryError);
        if (libraryError != IntPtr.Zero)
        {
            var description = libraryError.LocalizedDescription.ToManagedString("Unknown error");
            throw new Exception($"Failed to create library! {description}");
        }

        return library;
    }

    public IMaterialResources CreateMaterialResources(Material material)
    {
        var color = new[] { material.Color };
        var colorBufferLength = (ulong)Marshal.SizeOf<Vector4>();
        var colorBuffer = _device.NewBuffer(colorBufferLength, MTLResourceOptions.ResourceStorageModeShared);
        if (colorBuffer.NativePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to allocate material buffer.");
        }
        BufferHelper.CopyToBuffer(color, colorBuffer);

        var renderState = new RenderStateDescriptor(
            FillMode.Solid,
            CullMode.Back,
            depthTestEnabled: true,
            depthWriteEnabled: true,
            AbstractionBlendMode.Opaque);

        var pipelineKey = new PipelineKey(
            PassKind.Graphics,
            vertexEntryPoint: "vertexShader",
            pixelEntryPoint: "fragmentShader",
            computeEntryPoint: null,
            renderTargets: new(new[]
            {
                TextureFormat.Bgra8Unorm,
                TextureFormat.Rgba16Float,
                TextureFormat.Rgba8Unorm,
                TextureFormat.Rgba8Unorm
            }),
            depthStencil: new DepthStencilFormat(TextureFormat.D32Float),
            renderState: renderState,
            layout: GraphicsLayoutKind.Material);

        var shaderSource = _shaderCompiler.GetMetalSource(material.ShaderPath);
        var shaderBytes = Encoding.UTF8.GetBytes(shaderSource);
        var pipeline = _gfxDevice.GetOrCreatePipeline(pipelineKey, new ShaderBytecodeSet(shaderBytes, shaderBytes));
        var constantBuffer = new MetalBuffer($"{material.ShaderPath}_ColorBuffer", new(colorBufferLength, BufferUsage.Constant), colorBuffer);

        if (_linearSamplerHandle.IsValid == false)
        {
            var sampler = new SamplerDescriptor(FilterMode.Trilinear, AddressMode.Wrap, AddressMode.Wrap, AddressMode.Wrap);
            _linearSamplerHandle = _gfxDevice.GlobalTable.AllocateSampler(sampler);
        }

        return new MtlMaterialResources
        {
            Pipeline = pipeline,
            ConstantBuffer = constantBuffer,
            PipelineState = default,
            ColorBuffer = colorBuffer,
            AlbedoTexture = material.AlbedoTexture?.Resources?.ShaderResourceView ?? DescriptorHandle.Invalid,
            MetallicRoughnessTexture = material.MetallicRoughnessTexture?.Resources?.ShaderResourceView ?? DescriptorHandle.Invalid,
            NormalTexture = material.NormalTexture?.Resources?.ShaderResourceView ?? DescriptorHandle.Invalid,
            OcclusionTexture = material.OcclusionTexture?.Resources?.ShaderResourceView ?? DescriptorHandle.Invalid,
            EmissiveTexture = material.EmissiveTexture?.Resources?.ShaderResourceView ?? DescriptorHandle.Invalid,
            Sampler = _linearSamplerHandle
        };
    }

    public SkyboxResources CreateSkyboxResources(Texture environmentTexture, Mesh skyboxMesh)
    {
        if (environmentTexture is null)
        {
            throw new ArgumentNullException(nameof(environmentTexture));
        }

        if (environmentTexture.Resources is null)
        {
            throw new InvalidOperationException("Environment texture resources were not created.");
        }

        EnsureMeshResources(skyboxMesh);

        var renderState = new RenderStateDescriptor(
            FillMode.Solid,
            CullMode.Back,
            depthTestEnabled: false,
            depthWriteEnabled: false,
            AbstractionBlendMode.Opaque);

        var pipelineKey = new PipelineKey(
            PassKind.Graphics,
            vertexEntryPoint: "vertexShader",
            pixelEntryPoint: "fragmentShader",
            computeEntryPoint: null,
            renderTargets: new(new[]
            {
                TextureFormat.Bgra8Unorm,
                TextureFormat.Rgba16Float,
                TextureFormat.Rgba8Unorm,
                TextureFormat.Rgba8Unorm
            }),
            depthStencil: new DepthStencilFormat(TextureFormat.D32Float),
            renderState: renderState,
            layout: GraphicsLayoutKind.Skybox);

        var shaderSource = _shaderCompiler.GetMetalSource("skybox.slang");
        var shaderBytes = Encoding.UTF8.GetBytes(shaderSource);
        var pipeline = _gfxDevice.GetOrCreatePipeline(pipelineKey, new ShaderBytecodeSet(shaderBytes, shaderBytes));

        if (_skyboxSamplerHandle.IsValid == false)
        {
            var sampler = new SamplerDescriptor(FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp);
            _skyboxSamplerHandle = _gfxDevice.GlobalTable.AllocateSampler(sampler);
        }

        var (irradiance, prefiltered, brdfLut) = GenerateIblMaps(environmentTexture, _skyboxSamplerHandle);

        return new SkyboxResources
        {
            Pipeline = pipeline,
            EnvironmentHandle = environmentTexture.Resources.ShaderResourceView,
            Sampler = _skyboxSamplerHandle,
            Mesh = skyboxMesh,
            EnvironmentTexture = environmentTexture.Resources.Texture,
            IrradianceTexture = irradiance,
            PrefilteredEnvironment = prefiltered,
            BrdfLut = brdfLut
        };
    }

    private (IGfxTexture Irradiance, IGfxTexture Prefiltered, IGfxTexture BrdfLut) GenerateIblMaps(
        Texture environmentTexture,
        DescriptorHandle samplerHandle)
    {
        var envResources = environmentTexture.Resources
                           ?? throw new InvalidOperationException("Environment texture resources were not created.");
        var envTexture = envResources.Texture;

        const int irradianceSize = 64;
        const int prefilterWidth = 256;
        const int prefilterSliceHeight = 64;
        const int prefilterSlices = 6;
        const int brdfSize = 256;

        var irradianceDesc = new TextureDescriptor(
            irradianceSize,
            irradianceSize,
            TextureFormat.Rgba16Float,
            TextureUsage.ShaderResource | TextureUsage.UnorderedAccess);
        var prefilterDesc = new TextureDescriptor(
            prefilterWidth,
            prefilterSliceHeight * prefilterSlices,
            TextureFormat.Rgba16Float,
            TextureUsage.ShaderResource | TextureUsage.UnorderedAccess);
        var brdfDesc = new TextureDescriptor(
            brdfSize,
            brdfSize,
            TextureFormat.Rgba16Float,
            TextureUsage.ShaderResource | TextureUsage.UnorderedAccess);

        var irradianceTex = _gfxDevice.CreateTexture(irradianceDesc);
        var prefilterTex = _gfxDevice.CreateTexture(prefilterDesc);
        var brdfTex = _gfxDevice.CreateTexture(brdfDesc);

        // Irradiance
        {
            var pipeline = GetIblIrradiancePipeline();
            var commandList = _gfxDevice.BeginCompute();
            commandList.BindPipeline(pipeline);
            Span<uint> handles = stackalloc uint[7];
            handles[0] = envTexture.ShaderResourceView.Value;
            handles[1] = irradianceTex.UnorderedAccessView.Value;
            handles[2] = samplerHandle.Value;
            handles[3] = irradianceSize;
            handles[4] = irradianceSize;
            handles[5] = 1;
            handles[6] = irradianceSize;
            commandList.SetComputeConstants(0, MemoryMarshal.AsBytes(handles));

            var dispatchX = (uint)((irradianceSize + 7) / 8);
            var dispatchY = (uint)((irradianceSize + 7) / 8);
            commandList.Dispatch(dispatchX, dispatchY, 1);
            _gfxDevice.Submit(commandList);
        }

        // Prefilter (roughness slices stacked vertically)
        {
            var pipeline = GetIblPrefilterPipeline();
            var commandList = _gfxDevice.BeginCompute();
            commandList.BindPipeline(pipeline);
            Span<uint> handles = stackalloc uint[7];
            handles[0] = envTexture.ShaderResourceView.Value;
            handles[1] = prefilterTex.UnorderedAccessView.Value;
            handles[2] = samplerHandle.Value;
            handles[3] = prefilterWidth;
            handles[4] = prefilterSliceHeight * prefilterSlices;
            handles[5] = prefilterSlices;
            handles[6] = prefilterSliceHeight;
            commandList.SetComputeConstants(0, MemoryMarshal.AsBytes(handles));

            var dispatchX = (uint)((prefilterWidth + 7) / 8);
            var dispatchY = (uint)(((prefilterSliceHeight * prefilterSlices) + 7) / 8);
            commandList.Dispatch(dispatchX, dispatchY, 1);
            _gfxDevice.Submit(commandList);
        }

        // BRDF LUT
        {
            var pipeline = GetIblBrdfPipeline();
            var commandList = _gfxDevice.BeginCompute();
            commandList.BindPipeline(pipeline);
            Span<uint> handles = stackalloc uint[1];
            handles[0] = brdfTex.UnorderedAccessView.Value;
            commandList.SetComputeConstants(0, MemoryMarshal.AsBytes(handles));

            var dispatchX = (uint)((brdfSize + 7) / 8);
            var dispatchY = (uint)((brdfSize + 7) / 8);
            commandList.Dispatch(dispatchX, dispatchY, 1);
            _gfxDevice.Submit(commandList);
        }

        return (irradianceTex, prefilterTex, brdfTex);
    }

    private IGfxPipeline GetIblIrradiancePipeline()
    {
        if (_iblIrradiancePipeline is not null)
        {
            return _iblIrradiancePipeline;
        }

        var source = _shaderCompiler.GetMetalComputeSource("ibl_irradiance.compute.slang", "IblIrradianceCSMain");
        var shaderBytes = Encoding.UTF8.GetBytes(source);
        var pipelineKey = new PipelineKey(
            PassKind.Compute,
            vertexEntryPoint: null,
            pixelEntryPoint: null,
            computeEntryPoint: "IblIrradianceCSMain",
            renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
            depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
            renderState: default,
            layout: GraphicsLayoutKind.Default);
        _iblIrradiancePipeline = _gfxDevice.GetOrCreatePipeline(pipelineKey, new ShaderBytecodeSet(compute: shaderBytes));
        return _iblIrradiancePipeline;
    }

    private IGfxPipeline GetIblPrefilterPipeline()
    {
        if (_iblPrefilterPipeline is not null)
        {
            return _iblPrefilterPipeline;
        }

        var source = _shaderCompiler.GetMetalComputeSource("ibl_prefilter.compute.slang", "IblPrefilterCSMain");
        var shaderBytes = Encoding.UTF8.GetBytes(source);
        var pipelineKey = new PipelineKey(
            PassKind.Compute,
            vertexEntryPoint: null,
            pixelEntryPoint: null,
            computeEntryPoint: "IblPrefilterCSMain",
            renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
            depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
            renderState: default,
            layout: GraphicsLayoutKind.Default);
        _iblPrefilterPipeline = _gfxDevice.GetOrCreatePipeline(pipelineKey, new ShaderBytecodeSet(compute: shaderBytes));
        return _iblPrefilterPipeline;
    }

    private IGfxPipeline GetIblBrdfPipeline()
    {
        if (_iblBrdfLutPipeline is not null)
        {
            return _iblBrdfLutPipeline;
        }

        var source = _shaderCompiler.GetMetalComputeSource("ibl_brdf_lut.compute.slang", "IblBrdfCSMain");
        var shaderBytes = Encoding.UTF8.GetBytes(source);
        var pipelineKey = new PipelineKey(
            PassKind.Compute,
            vertexEntryPoint: null,
            pixelEntryPoint: null,
            computeEntryPoint: "IblBrdfCSMain",
            renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
            depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
            renderState: default,
            layout: GraphicsLayoutKind.Default);
        _iblBrdfLutPipeline = _gfxDevice.GetOrCreatePipeline(pipelineKey, new ShaderBytecodeSet(compute: shaderBytes));
        return _iblBrdfLutPipeline;
    }

    public ITextureResources CreateTextureResources(Texture texture)
    {
        if (texture is null)
        {
            throw new ArgumentNullException(nameof(texture));
        }

        if (texture.PixelData is null || texture.PixelData.Length == 0)
        {
            throw new ArgumentException("Texture must contain pixel data.", nameof(texture));
        }

        var descriptor = new TextureDescriptor(
            texture.Width,
            texture.Height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.ShaderResource);

        var gfxTexture = _gfxDevice.CreateTexture(descriptor);
        if (gfxTexture is not MetalTexture metalTexture)
        {
            throw new InvalidOperationException("Metal renderer expected a Metal texture.");
        }

        UploadTextureData(metalTexture.Texture, texture);

        return new MetalTextureResources
        {
            Texture = metalTexture,
            ShaderResourceView = metalTexture.ShaderResourceView
        };
    }

    public IGfxDevice GetGfxDevice()
    {
        return _gfxDevice;
    }

    public Int2 GetFrameBufferSize()
    {
        var width = _hasDrawableSize ? (int)_drawableWidth : _width;
        var height = _hasDrawableSize ? (int)_drawableHeight : _height;
        return new Int2(width, height);
    }

    public Int2 GetWindowSize()
    {
        if (_window is null)
        {
            return new Int2(_width, _height);
        }

        int windowWidth = 0;
        int windowHeight = 0;
        _sdl.GetWindowSize(_window, ref windowWidth, ref windowHeight);
        if (windowWidth <= 0 || windowHeight <= 0)
        {
            return new Int2(_width, _height);
        }

        return new Int2(windowWidth, windowHeight);
    }

    public void BeginFrame()
    {
        UpdateDrawableSize();
    }

    public void Render(
        float deltaTime,
        RenderGraphResourceRegistry resourceRegistry,
        RenderGraphResourceHandle backBuffer,
        RenderGraphResourceHandle presentedTexture,
        UiFrameData uiFrame)
    {
        if (_useRenderGraph)
        {
            return;
        }

        var backbufferTexture = resourceRegistry.GetTexture(backBuffer) as MetalBackbufferTexture;
        var lightingTexture = resourceRegistry.GetTexture(presentedTexture) as MetalTexture;
        if (backbufferTexture is null || lightingTexture is null)
        {
            return;
        }

        if (_commandQueue.NativePtr == IntPtr.Zero || backbufferTexture.Drawable.NativePtr == IntPtr.Zero)
        {
            return;
        }

        var source = lightingTexture.Texture;
        var destination = backbufferTexture.Drawable.Texture;
        if (source.NativePtr == IntPtr.Zero || destination.NativePtr == IntPtr.Zero)
        {
            return;
        }

        var commandBuffer = _commandQueue.CommandBuffer();
        var blit = commandBuffer.BlitCommandEncoder();
        var origin = new MTLOrigin { x = 0, y = 0, z = 0 };
        var width = Math.Min(source.Width, destination.Width);
        var height = Math.Min(source.Height, destination.Height);
        var size = new MTLSize { width = width, height = height, depth = 1 };
        blit.CopyFromTexture(source, 0, 0, origin, size, destination, 0, 0, origin);
        blit.EndEncoding();

        commandBuffer.PresentDrawable(backbufferTexture.Drawable);
        commandBuffer.Commit();
    }

    public RenderGraphResourceHandle ImportBackbuffer(RenderGraphResourceRegistry registry, int width, int height)
    {
        var drawablePtr = ObjectiveC.IntPtr_objc_msgSend(_metalLayer.NativePtr, NextDrawableSelector);
        if (drawablePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to acquire CAMetalDrawable.");
        }

        _currentDrawable = new CAMetalDrawable(drawablePtr);

        var descriptor = new TextureDescriptor(
            Math.Max(width, 1),
            Math.Max(height, 1),
            TextureFormat.Bgra8Unorm,
            TextureUsage.RenderTarget,
            Vector4.Zero);

        var backbuffer = new MetalBackbufferTexture(_currentDrawable, descriptor);
        return registry.ImportTexture(backbuffer, takeOwnership: false, initialState: ResourceState.RenderTarget);
    }

    public void ExecuteGBufferPass(RenderGraphContext context, RenderGraphFrameResources resources)
    {
        throw new NotSupportedException("Use render-graph pass execution instead of direct Metal GBuffer calls.");
    }

    public void ExecuteDeferredPass(RenderGraphContext context, RenderGraphFrameResources resources)
    {
        throw new NotSupportedException("Use render-graph pass execution instead of direct Metal deferred calls.");
    }

    private static void UploadTextureData(MTLTexture texture, Texture source)
    {
        var bytesPerPixel = GetBytesPerPixel(texture.PixelFormat);
        var expectedBytes = source.Width * source.Height * bytesPerPixel;
        if (source.PixelData.Length < expectedBytes)
        {
            throw new ArgumentException(
                $"Texture pixel data is smaller than the expected size for {texture.PixelFormat}.",
                nameof(source));
        }

        var origin = new MTLOrigin { x = 0, y = 0, z = 0 };
        var size = new MTLSize { width = (ulong)source.Width, height = (ulong)source.Height, depth = 1 };
        var region = new MTLRegion { origin = origin, size = size };
        var bytesPerRow = (ulong)(source.Width * bytesPerPixel);

        fixed (byte* ptr = source.PixelData)
        {
            texture.ReplaceRegion(region, 0, (IntPtr)ptr, bytesPerRow);
        }
    }

    private static int GetBytesPerPixel(MTLPixelFormat format) => format switch
    {
        MTLPixelFormat.RGBA8Unorm => 4,
        MTLPixelFormat.BGRA8Unorm => 4,
        MTLPixelFormat.RGBA8UnormsRGB => 4,
        MTLPixelFormat.BGRA8UnormsRGB => 4,
        MTLPixelFormat.RGBA16Float => 8,
        MTLPixelFormat.RGBA32Float => 16,
        _ => throw new InvalidOperationException($"Unsupported pixel format for upload: {format}.")
    };

    private MTLRenderPipelineState CreateRenderPipeline(MTLLibrary shaderLibrary)
    {
        var vertexShader = shaderLibrary.NewFunction(NSStringHelper.From("vertexShader"));
        var fragmentShader = shaderLibrary.NewFunction(NSStringHelper.From("fragmentShader"));

        var pipeline = new MTLRenderPipelineDescriptor();
        pipeline.VertexFunction = vertexShader;
        pipeline.FragmentFunction = fragmentShader;
        pipeline.VertexDescriptor = CreateVertexDescriptor();
        pipeline.DepthAttachmentPixelFormat = MTLPixelFormat.Depth32Float;

        var colorAttachment = pipeline.ColorAttachments.Object(0);
        colorAttachment.PixelFormat = MTLPixelFormat.BGRA8Unorm;
        pipeline.ColorAttachments.SetObject(colorAttachment, 0);

        var pipelineStateError = new NSError(IntPtr.Zero);
        var pipelineState = _device.NewRenderPipelineState(pipeline, ref pipelineStateError);
        if (pipelineStateError != IntPtr.Zero)
        {
            throw new Exception($"Failed to create render pipeline state! {pipelineStateError.LocalizedDescription.ToManagedString()}");
        }

        return pipelineState;
    }

    private static MTLVertexDescriptor CreateVertexDescriptor()
    {
        var descriptor = new MTLVertexDescriptor();

        var attributes = descriptor.Attributes;
        var positionAttribute = attributes.Object(0);
        positionAttribute.Format = MTLVertexFormat.Float4;
        positionAttribute.Offset = 0;
        positionAttribute.BufferIndex = 0;
        attributes.SetObject(positionAttribute, 0);

        var normalAttribute = attributes.Object(1);
        normalAttribute.Format = MTLVertexFormat.Float4;
        normalAttribute.Offset = (ulong)Marshal.SizeOf<Vector4>();
        normalAttribute.BufferIndex = 0;
        attributes.SetObject(normalAttribute, 1);

        var layouts = descriptor.Layouts;
        var layout = layouts.Object(0);
        layout.Stride = (ulong)Marshal.SizeOf<VertexData>();
        layout.StepFunction = MTLVertexStepFunction.PerVertex;
        layout.StepRate = 1;
        layouts.SetObject(layout, 0);

        return descriptor;
    }

    private MeshResources UploadMesh(Mesh mesh)
    {
        if (mesh.Vertices.Length == 0)
        {
            throw new InvalidOperationException("Mesh must contain vertex data.");
        }

        if (mesh.Normals.Length != mesh.Vertices.Length ||
            mesh.UVs.Length != mesh.Vertices.Length ||
            mesh.Tangents.Length != mesh.Vertices.Length)
        {
            throw new InvalidOperationException("Mesh must contain normals, UVs, and tangents per vertex.");
        }

        var vertexCount = mesh.Vertices.Length;
        var vertexData = new VertexData[vertexCount];
        for (var i = 0; i < vertexCount; i++)
        {
            var normal = mesh.Normals[i];
            vertexData[i] = new VertexData
            {
                Position = mesh.Vertices[i],
                Normal = normal,
                UV = mesh.UVs[i],
                Tangent = mesh.Tangents[i]
            };
        }

        var vertexBufferLength = (ulong)(vertexData.Length * Marshal.SizeOf<VertexData>());
        var vertexBuffer = _device.NewBuffer(vertexBufferLength, MTLResourceOptions.ResourceStorageModeShared);
        if (vertexBuffer.NativePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to allocate vertex buffer.");
        }
        BufferHelper.CopyToBuffer(vertexData, vertexBuffer);

        var indexBufferLength = (ulong)(mesh.Indices.Length * sizeof(uint));
        var indexBuffer = _device.NewBuffer(indexBufferLength, MTLResourceOptions.ResourceStorageModeShared);
        if (indexBuffer.NativePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to allocate index buffer.");
        }
        BufferHelper.CopyToBuffer(mesh.Indices, indexBuffer);

        var vertexBufferAbstraction = new MetalBuffer("MeshVertexBuffer", new(vertexBufferLength, BufferUsage.Vertex), vertexBuffer);
        var indexBufferAbstraction = new MetalBuffer("MeshIndexBuffer", new(indexBufferLength, BufferUsage.Index), indexBuffer);

        mesh.VertexBuffer = vertexBufferAbstraction;
        mesh.IndexBuffer = indexBufferAbstraction;
        mesh.StrideInBytes = (uint)Marshal.SizeOf<VertexData>();
        mesh.IndexCount = (uint)mesh.Indices.Length;

        return new MeshResources(vertexBuffer, indexBuffer, (ulong)mesh.Indices.Length);
    }

    public void EnsureMeshResources(Mesh mesh)
    {
        if (_meshResources.TryGetValue(mesh, out var resources) == false)
        {
            resources = UploadMesh(mesh);
            _meshResources[mesh] = resources;
        } 
    }

    public IImGuiRenderer GetImGuiRenderer()
    {
        return _imguiRenderer;
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
                    throw new ArgumentOutOfRangeException(nameof(command.Type), command.Type, "Unsupported render command type.");
            }
        }
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
        _drawCommands.Add(new DrawInstruction(mesh, material, payload.Transform));
    }

    private void HandleSetCameraCommand(RenderCommand command)
    {
        var payload = command.ReadPayload<RenderCommand.SetCameraPayload>();

        _camera = payload.Camera;
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

    private static bool NearlyEqual(double a, double b)
    {
        const double epsilon = 0.5;
        return Math.Abs(a - b) < epsilon;
    }
}
