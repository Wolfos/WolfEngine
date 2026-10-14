using System.Diagnostics;
using CoreAnimation;
using Microsoft.Extensions.DependencyInjection;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Backend.Metal;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Runtime.iOS;

/// <summary>
/// Drives the engine's own Metal renderer from the display, presenting into this app's view. Each frame imports the
/// drawable through the renderer, as the render graph does, and clears it. The colour pulses green when every engine
/// probe passed and red when any failed.
/// </summary>
internal sealed class SpikeRenderer : IDisposable
{
	private readonly MetalLayerView _view;
	private readonly bool _probesPassed;
	private readonly ServiceProvider _services;
	private readonly IRenderer _renderer;
	private readonly HostMetalSurface _surface;
	private readonly Stopwatch _clock = Stopwatch.StartNew();
	private CADisplayLink? _displayLink;
	private bool _started;
	private long _frameIndex;
	private TimeSpan _lastReport;

	public SpikeRenderer(MetalLayerView view, bool probesPassed)
	{
		_view = view;
		_probesPassed = probesPassed;
		_surface = new HostMetalSurface(view.MetalLayer.Handle.Handle);
		view.DrawableSizeChanged += _ => ReportSize();
		ReportSize();

		var services = new ServiceCollection();
		global::WolfEngine.WolfEngine.ConfigureServices(services);
		services.AddHostMetalSurface(_surface);
		services.AddSingleton<IShaderProvider>(new NoShaders());
		_services = services.BuildServiceProvider();
		_renderer = _services.GetRequiredService<IRenderer>();
	}

	public void Start()
	{
		if (_displayLink is not null)
		{
			return;
		}

		if (_started == false)
		{
			_renderer.Start(
				() => Console.WriteLine($"[spike] engine renderer started, ray tracing: {_renderer.GetGfxDevice().SupportsRayTracing}"),
				_ => { },
				_ => RenderFrame());
			_started = true;
		}

		_displayLink = CADisplayLink.Create(() => _renderer.RunFrame());
		_displayLink.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Common);
	}

	public void Stop()
	{
		_displayLink?.Invalidate();
		_displayLink = null;
		if (_started)
		{
			_renderer.GetGfxDevice().WaitForIdle();
		}
	}

	private void ReportSize()
	{
		var bounds = _view.Bounds;
		var scale = _view.ContentScaleFactor;
		_surface.SetSize(
			new Int2((int)Math.Round(bounds.Width * scale), (int)Math.Round(bounds.Height * scale)),
			new Int2((int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height)));
	}

	private void RenderFrame()
	{
		_renderer.BeginFrame();
		var size = _renderer.GetFrameBufferSize();
		var registry = new RenderGraphResourceRegistry();
		var handle = _renderer.ImportBackbuffer(registry, size.X, size.Y);
		var backbuffer = registry.GetTexture(handle);
		var width = backbuffer.Descriptor.Width;
		var height = backbuffer.Descriptor.Height;

		var device = _renderer.GetGfxDevice();
		var commandList = device.BeginGraphics();
		commandList.BeginPass(new PassTargets([new ColorTargetBinding(backbuffer)]), new Viewport(0, 0, width, height));
		commandList.ClearColorAttachment(0, GetClearColor());

		// The Metal backend opens its render encoder lazily on the first encoded command, and a pass that
		// only clears encodes none. Setting the scissor opens the encoder so the clear happens.
		commandList.SetScissorRect(new RectInt(0, 0, width, height));
		commandList.EndPass();
		device.Submit(commandList, GpuSubmissionKind.PrimaryFrame);

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
		Console.WriteLine($"[spike] frame {_frameIndex}, {width}x{height}, window {_renderer.GetWindowSize().X}x{_renderer.GetWindowSize().Y}");
	}

	public void Dispose()
	{
		Stop();
		if (_started)
		{
			_renderer.Stop();
		}

		_services.Dispose();
	}

	/// <summary>The spike draws no shaded geometry, so it needs no shaders.</summary>
	private sealed class NoShaders : IShaderProvider
	{
		public long Revision => 0;

		public event Action<long>? RevisionChanged
		{
			add
			{
			}

			remove
			{
			}
		}

		public CompiledShaderArtifact GetArtifact(ShaderRequest request)
		{
			throw new NotSupportedException("The renderer spike has no shaders.");
		}

		public void SetProjectRoot(string? projectRootPath)
		{
		}

		public ShaderReloadResult Reload(GraphicsBackendKind backendKind)
		{
			throw new NotSupportedException("The renderer spike has no shaders.");
		}
	}
}
