using System.Diagnostics;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using WolfEngine;
using WolfEngine.ECS;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.Shaders;
using WolfEngine.Rendering.UI;
using Wolfie.IAE.Projects;
using Wolfie.IAE.UI;
using Wolfie.IAE.UnityAssets;

namespace Wolfie.IAE;

public sealed class WolfieApplication : IDisposable
{
	private readonly ServiceProvider _services;
	private readonly RenderConfig _renderConfig = new();
	private readonly WorldTransform _cameraTransform = new()
	{
		LocalToWorld = Matrix4x4.Identity,
		WorldToLocal = Matrix4x4.Identity
	};
	private volatile bool _running;

	private WolfieApplication(ServiceProvider services) => _services = services;

	public static WolfieApplication Create()
	{
		var services = new ServiceCollection();
		var engineRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "WolfEngine"));
		global::WolfEngine.WolfEngine.ConfigureServices(services, new EngineShaderOptions { EngineContentRoot = engineRoot });
		services.AddSingleton<WolfieProjectService>();
		services.AddSingleton<WolfiePreferences>();
		services.AddSingleton<UnityAssetScanner>();
		services.AddSingleton<IImageLoader, ImageLoader>();
		services.AddSingleton<IIconManager, IconManager>();
		services.AddSingleton<WolfieGui>();
		return new WolfieApplication(services.BuildServiceProvider());
	}

	public void Run()
	{
		var thread = new Thread(UiLoop) { IsBackground = true, Name = "WolfieUI" };
		_running = true;
		thread.Start();
		try { _services.GetRequiredService<IRenderPipeline>().Run(); }
		finally { _running = false; thread.Join(); }
	}

	private void UiLoop()
	{
		var frames = _services.GetRequiredService<IUiFrameProvider>();
		var renderer = _services.GetRequiredService<IRenderer>();
		var graph = _services.GetRequiredService<RenderGraph>();
		var coordinator = _services.GetRequiredService<EditorFrameCoordinator>();
		var renderPipeline = _services.GetRequiredService<IRenderPipeline>();
		var gui = _services.GetRequiredService<WolfieGui>();
		var timer = Stopwatch.StartNew();
		var last = timer.Elapsed;
		while (_running)
		{
			var now = timer.Elapsed;
			frames.NewFrame((float)(now - last).TotalSeconds, renderer.GetWindowSize(), graph.GetFrameBufferSize());
			last = now;
			frames.RunGui(gui.Draw);
			PublishSnapshot(renderPipeline, renderer.GetFrameBufferSize());
			coordinator.PublishCompletedFrame();
			Thread.Sleep(1);
		}
	}

	private void PublishSnapshot(IRenderPipeline renderPipeline, Int2 resolution)
	{
		var camera = new Camera
		{
			ScreenResolution = new Int2(Math.Max(resolution.X, 1), Math.Max(resolution.Y, 1))
		};
		camera.SetPerspective(70.0f);
		renderPipeline.PublishSnapshot(camera, _cameraTransform, _renderConfig, Array.Empty<World>());
	}

	public void Dispose() => _services.Dispose();
}
