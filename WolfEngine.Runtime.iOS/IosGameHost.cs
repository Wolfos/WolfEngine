using CoreAnimation;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Gameplay;
using Microsoft.Extensions.DependencyInjection;
using WolfEngine.Mathematics;
using WolfEngine.Profiling;
using WolfEngine.Rendering.Backend.Metal;

namespace WolfEngine.Runtime.iOS;

/// <summary>Something the scene delegate starts when the app becomes active and stops when it resigns.</summary>
internal interface IAppFrameHost : IDisposable
{
	void Start();

	void Stop();
}

/// <summary>
/// Runs the cooked game bundled in the app. Gameplay steps on its own thread, as on desktop, while the render
/// pipeline renders one frame per display refresh on the main thread, which UIKit owns.
/// </summary>
/// <remarks>
/// Physics stays off until Jolt is built for iOS. The gameplay assembly cannot be loaded at runtime under
/// NativeAOT, and the game's own gameplay project does not build for iOS yet, so an empty module stands in.
/// </remarks>
internal sealed class IosGameHost : IAppFrameHost
{
	private readonly MetalLayerView _view;
	private readonly HostMetalSurface _surface;
	private readonly WolfPackCatalog _catalog;
	private readonly RuntimeHost _host;
	private readonly IosPerformanceReport? _performanceReport;
	private CADisplayLink? _displayLink;
	private Thread? _gameThread;
	private volatile bool _gameRunning;
	private bool _renderingStarted;

	private IosGameHost(MetalLayerView view, HostMetalSurface surface, WolfPackCatalog catalog, RuntimeHost host)
	{
		_view = view;
		_surface = surface;
		_catalog = catalog;
		_host = host;
		_performanceReport = IosPerformanceReport.CreateIfRequested(host.Services.GetRequiredService<GpuProfiler>());
	}

	/// <summary>The bundled game's manifest, or null when the app was built without content.</summary>
	public static string? FindBundledManifest()
	{
		var path = Path.Combine(AppContext.BaseDirectory, "Content", "bootstrap.wolfmanifest");

		return File.Exists(path) ? path : null;
	}

	public static IosGameHost Create(MetalLayerView view, string manifestPath)
	{
		var surface = new HostMetalSurface(view.MetalLayer.Handle.Handle);
		var catalog = new WolfPackCatalog(manifestPath);
		try
		{
			var host = RuntimeHost.Create(new RuntimeHostOptions
			{
				Catalog = catalog,
				Gameplay = new EmptyGameplayModule(),
				EnablePhysics = false,
				ConfigureServices = services => services.AddHostMetalSurface(surface)
			});
			var gameHost = new IosGameHost(view, surface, catalog, host);
			view.DrawableSizeChanged += _ => gameHost.ReportSize();
			gameHost.ReportSize();

			return gameHost;
		}
		catch
		{
			catalog.Dispose();
			throw;
		}
	}

	public void Start()
	{
		if (_renderingStarted == false)
		{
			_host.RenderPipeline.Start(() => Console.WriteLine("runtime renderer ready"));
			_renderingStarted = true;
		}

		if (_gameThread is null)
		{
			_gameRunning = true;
			_gameThread = new Thread(RunGameLoop)
			{
				IsBackground = true,
				Name = "GameThread"
			};
			_gameThread.Start();
		}

		if (_displayLink is null)
		{
			_displayLink = CADisplayLink.Create(RenderFrame);
			_displayLink.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Common);
		}
	}

	/// <summary>Pauses rendering. iOS terminates apps that submit GPU work in the background.</summary>
	public void Stop()
	{
		_displayLink?.Invalidate();
		_displayLink = null;
	}

	public void Dispose()
	{
		Stop();
		_gameRunning = false;
		_gameThread?.Join();
		if (_renderingStarted)
		{
			_host.RenderPipeline.Stop();
		}

		_host.Dispose();
		_catalog.Dispose();
	}

	private void RenderFrame()
	{
		if (_host.RenderPipeline.RenderFrame() == false)
		{
			Console.WriteLine("runtime render pipeline stopped; no more frames will be presented");
			Stop();

			return;
		}

		_performanceReport?.OnFrame(_displayLink?.Timestamp ?? 0.0);
	}

	private void RunGameLoop()
	{
		try
		{
			while (_gameRunning && _host.Tick())
			{
				Thread.Sleep(0);
			}
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine($"runtime game loop failed: {exception}");
		}
		finally
		{
			try
			{
				_host.Stop();
			}
			catch (Exception exception)
			{
				Console.Error.WriteLine($"runtime gameplay cleanup failed: {exception}");
			}
		}
	}

	private void ReportSize()
	{
		var bounds = _view.Bounds;
		var scale = _view.ContentScaleFactor;
		var framebufferSize = new Int2((int)Math.Round(bounds.Width * scale), (int)Math.Round(bounds.Height * scale));
		_surface.SetSize(
			framebufferSize,
			new Int2((int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height)));
		Console.WriteLine($"runtime surface {framebufferSize.X}x{framebufferSize.Y}");
	}

	/// <summary>Stands in for the game's gameplay until it can be compiled into the app.</summary>
	private sealed class EmptyGameplayModule : IGameplayModule
	{
		public void OnLoaded(World world)
		{
		}

		public void OnUnloading(World world)
		{
		}

		public void PhysicsUpdate(float fixedDeltaTime, World world)
		{
		}

		public void Update(float deltaTime, World world)
		{
		}
	}
}
