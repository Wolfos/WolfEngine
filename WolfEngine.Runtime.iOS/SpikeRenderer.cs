using System.Diagnostics;
using CoreAnimation;
using SharpMetal.Metal;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Backend.Metal;
using SharpCAMetalDrawable = SharpMetal.QuartzCore.CAMetalDrawable;
using SharpCAMetalLayer = SharpMetal.QuartzCore.CAMetalLayer;

namespace WolfEngine.Runtime.iOS;

/// <summary>
/// Clears the screen every display refresh through the engine's own Metal backend.
/// The colour pulses green when every engine probe passed and red when any failed.
/// </summary>
internal sealed class SpikeRenderer : IDisposable
{
	private readonly MetalLayerView _view;
	private readonly SharpCAMetalLayer _layer;
	private readonly MetalDevice _device;
	private readonly bool _probesPassed;
	private readonly Stopwatch _clock = Stopwatch.StartNew();
	private CADisplayLink? _displayLink;
	private long _frameIndex;
	private TimeSpan _lastReport;

	public SpikeRenderer(MetalLayerView view, bool probesPassed)
	{
		_view = view;
		_probesPassed = probesPassed;

		var device = MTLDevice.CreateSystemDefaultDevice();
		if (device.NativePtr == IntPtr.Zero)
		{
			throw new InvalidOperationException("This device has no Metal GPU.");
		}

		Console.WriteLine($"[spike] Metal device: {device.Name}, ray tracing: {device.SupportsRaytracing}");

		// The engine's SharpMetal wrappers drive the UIKit-owned layer by its Objective-C pointer,
		// the same way WolfRendererMetal drives the layer SDL creates on macOS.
		_layer = new SharpCAMetalLayer(view.MetalLayer.Handle.Handle);
		_layer.Device = device;
		_layer.PixelFormat = MTLPixelFormat.BGRA8Unorm;
		_layer.FramebufferOnly = false;

		_device = new MetalDevice(device);
	}

	public void Start()
	{
		if (_displayLink is not null)
		{
			return;
		}

		_displayLink = CADisplayLink.Create(RenderFrame);
		_displayLink.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Common);
	}

	public void Stop()
	{
		_displayLink?.Invalidate();
		_displayLink = null;
		_device.WaitForIdle();
	}

	private void RenderFrame()
	{
		var drawableSize = _view.MetalLayer.DrawableSize;
		var width = (int)drawableSize.Width;
		var height = (int)drawableSize.Height;
		if (width <= 0 || height <= 0)
		{
			return;
		}

		var drawable = _layer.NextDrawable;
		if (drawable.NativePtr == IntPtr.Zero)
		{
			return;
		}

		var backbuffer = new MetalBackbufferTexture(
			drawable,
			new TextureDescriptor(width, height, TextureFormat.Bgra8Unorm, TextureUsage.RenderTarget));

		var commandList = _device.BeginGraphics();
		commandList.BeginPass(
			new PassTargets([new ColorTargetBinding(backbuffer)]),
			new Viewport(0, 0, width, height));
		commandList.ClearColorAttachment(0, GetClearColor());

		// The Metal backend opens its render encoder lazily on the first encoded command, and a pass that
		// only clears encodes none. Setting the scissor opens the encoder so the clear happens.
		commandList.SetScissorRect(new RectInt(0, 0, width, height));
		commandList.EndPass();
		_device.Submit(commandList, GpuSubmissionKind.PrimaryFrame);

		_frameIndex++;
		ReportFrameRate(width, height);
	}

	private ColorRGBA GetClearColor()
	{
		var pulse = 0.35f + 0.25f * MathF.Sin((float)_clock.Elapsed.TotalSeconds * 2.0f);
		if (_probesPassed)
		{
			return new ColorRGBA(0.05f, pulse, 0.1f, 1.0f);
		}

		return new ColorRGBA(pulse, 0.05f, 0.05f, 1.0f);
	}

	private void ReportFrameRate(int width, int height)
	{
		var now = _clock.Elapsed;
		if (now - _lastReport < TimeSpan.FromSeconds(5))
		{
			return;
		}

		_lastReport = now;
		Console.WriteLine(
			$"[spike] frame {_frameIndex}, {width}x{height}, " +
			$"submitted {_device.LastSubmittedId}, completed {_device.CompletedId}");
	}

	public void Dispose()
	{
		Stop();
	}
}
