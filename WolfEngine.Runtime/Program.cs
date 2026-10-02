using System.Diagnostics;
using WolfEngine.Input;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Gameplay;
using WolfEngine.Mathematics;
using WolfEngine.Physics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.Shaders;
using WolfEngine.Rendering.UI;
using WolfEngine.Audio;
using WolfEngine.UI;

namespace WolfEngine.Runtime;

public static class Program
{
	public static int Main(string[] args)
	{
		try
		{
			return Run(RuntimeOptions.Parse(args));
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine($"runtime failed: {exception}");
			return 1;
		}
	}

	private static int Run(RuntimeOptions options)
	{
		var manifestPath = options.Manifest ?? Path.Combine(AppContext.BaseDirectory, "Content", "bootstrap.wolfmanifest");
		using var catalog = new WolfPackCatalog(manifestPath);
		Console.WriteLine($"runtime mounted {catalog.Manifest.Packs.Count} cooked packs");

		var expectedTarget = CurrentRid();
		if (!string.Equals(catalog.Manifest.Target, expectedTarget, StringComparison.Ordinal))
			throw new PlatformNotSupportedException(
				$"Build target '{catalog.Manifest.Target}' cannot run on '{expectedTarget}'.");

		var settings = JsonSerializer.Deserialize<CookedRuntimeSettings>(
			catalog.Read(catalog.Manifest.RuntimeSettingsId),
			AssetJson.SerializerOptions) ?? throw new InvalidDataException("Runtime settings are invalid.");
		if (options.Frames > 0)
			Screen.VSyncEnabled = false;

		var gameplaySymbols = catalog.Manifest.GameplaySymbolsId == Guid.Empty ? null : catalog.Read(catalog.Manifest.GameplaySymbolsId);
		var (_, gameplay) = new GameplayAssemblyLoader().Load(
			catalog.Read(catalog.Manifest.GameplayAssemblyId),
			gameplaySymbols);

		var services = new ServiceCollection();
		WolfEngine.ConfigureServices(services);
		services.AddWolfEngineGameplayUi();
		services.AddSingleton(new RenderPresentationOptions { OutputMode = RenderOutputMode.FullWindow });
		var packagedShaders = new PackagedShaderProvider(catalog);
		services.AddSingleton<IShaderProvider>(packagedShaders);
		services.AddSingleton<IPackagedShaderProvider>(packagedShaders);

		var nullUi = new RuntimeNullUi();
		services.AddSingleton<IUiFrameProvider>(nullUi);
		services.AddSingleton<IImGuiInputSink>(nullUi);
		services.AddSingleton<IImGuiRenderer>(NullImGuiRenderer.Instance);
		services.AddSingleton(catalog);
		services.AddSingleton<IFontContentProvider>(new WolfPackFontContentProvider(catalog));
		services.AddSingleton<IAudioContentProvider>(new WolfPackAudioContentProvider(catalog));
		services.AddSingleton<AudioService>();
		services.AddSingleton<IAudioService>(provider => provider.GetRequiredService<AudioService>());
		services.AddSingleton<IAudioRuntime>(provider => provider.GetRequiredService<AudioService>());
		services.AddSingleton<IMaterialTypeRegistry, MaterialTypeRegistry>();
		services.AddSingleton<RuntimeAssetStore>();
		services.AddSingleton<IRuntimeAssetStore>(provider => provider.GetRequiredService<RuntimeAssetStore>());
		services.AddSingleton<IAssetInstanceRegistry>(provider => provider.GetRequiredService<RuntimeAssetStore>());
		services.AddSingleton<IRuntimeSceneLoader, RuntimeSceneLoader>();
		services.AddSingleton<RigidbodySystem>();

		using var provider = services.BuildServiceProvider();
		var renderer = provider.GetRequiredService<IRenderer>();
		renderer.SetWindowSize(new Int2(settings.Width, settings.Height));
		var assetStore = provider.GetRequiredService<IAssetInstanceRegistry>();
		AssetDatabase.SetInstanceRegistry(assetStore);

		var world = provider.GetRequiredService<IRuntimeSceneLoader>().Load(catalog.Manifest.InitialSceneId);
		Console.WriteLine($"runtime loaded scene {catalog.Manifest.InitialSceneId:D}");
		var worldManager = provider.GetRequiredService<IWorldManager>();
		worldManager.AddSystem<CameraResolutionUpdater>();
        worldManager.AddSystem(new AnimationRenderResourceLifecycle(provider.GetRequiredService<RenderGraph>()));
		// Before TransformSystem, so exposed bone sockets propagate in the frame they are posed.
		worldManager.AddSystem<AnimationSystem>();
		// Register before TransformSystem so interpolated child poses propagate this frame.
		worldManager.AddSystem(new VehicleSystem(), SystemExecutionGroup.Gameplay);
		worldManager.AddSystem(provider.GetRequiredService<RigidbodySystem>(), SystemExecutionGroup.Gameplay);
		worldManager.AddSystem<TransformSystem>();
		var renderPipeline = provider.GetRequiredService<IRenderPipeline>();
		var primaryView = provider.GetRequiredService<RenderGraph>().CreateView(
			new RenderViewDescriptor(world, "game", RenderViewOutput.Backbuffer));
		var sceneRequests = provider.GetRequiredService<SceneLoadRequests>();
		var session = new GameplayWorldSession(worldManager, gameplay, provider,
			next =>
			{
				if (!provider.GetRequiredService<RenderGraph>().RebindView(primaryView, next))
					throw new InvalidOperationException("Runtime render view is unavailable.");
			}, provider.GetRequiredService<IAudioRuntime>().StopAll);
		var running = true;
		Exception? gameError = null;
		
		var gameThread = new Thread(() =>
		{
			try
			{
				session.Replace(world);
				GameLoop(provider, session, primaryView, gameplay, settings, options, ref running);
			}
			catch (Exception exception)
			{
				gameError = exception;
				Console.Error.WriteLine($"runtime game loop failed: {exception}");
			}
			finally
			{
				try { session.Stop(); }
				catch (Exception exception) { gameError ??= exception; Console.Error.WriteLine($"runtime gameplay cleanup failed: {exception}"); }
				sceneRequests.CancelPending();
				provider.GetRequiredService<EditorFrameCoordinator>().RequestShutdown();
				renderer.RequestShutdown();
			}
		})
		{
			IsBackground = true,
			Name = "GameThread"
		};
		gameThread.Start();
		
		try
		{
			renderPipeline.Run(() => Console.WriteLine("runtime renderer ready"));
		}
		finally
		{
			running = false;
			// Teardown can dispatch GPU retirement after the render loop exits. Service it until the game thread stops.
			var dispatcher = provider.GetRequiredService<global::WolfEngine.Utility.IMainThreadDispatcher>();
			while (!gameThread.Join(1)) dispatcher.ExecutePending();
			AssetDatabase.ClearInstanceRegistry();
		}

		if (gameError is not null)
			throw gameError;

		return 0;
	}

	private static void GameLoop(
		IServiceProvider services,
		GameplayWorldSession session,
		RenderViewId view,
		IGameplayModule gameplay,
		CookedRuntimeSettings settings,
		RuntimeOptions options,
		ref bool running)
	{
		var manager = services.GetRequiredService<IWorldManager>();
		var pipeline = services.GetRequiredService<IRenderPipeline>();
		var renderer = services.GetRequiredService<IRenderer>();
		var frameCoordinator = services.GetRequiredService<EditorFrameCoordinator>();
		var audio = services.GetRequiredService<IAudioRuntime>();
		var rigidbodySystem = services.GetRequiredService<RigidbodySystem>();
		var stopwatch = Stopwatch.StartNew();
		var last = stopwatch.Elapsed;
		var accumulator = 0f;
		var frames = 0;
		var input = services.GetRequiredService<IInputSystem>();
		var pointerRouter = services.GetRequiredService<IPointerInputRouter>();
		var requests = services.GetRequiredService<SceneLoadRequests>();
		var loader = services.GetRequiredService<IRuntimeSceneLoader>();
		while (running)
		{
			if (requests.TryTake(session.World, out var request))
			{
				try
				{
					var next = loader.Load(request.SceneId);
					if (!TryGetCamera(next, out _, out _)) throw new InvalidOperationException("Scene has no active camera.");
					session.Replace(next);
					accumulator = 0;
					last = stopwatch.Elapsed;
					request.Complete();
				}
				catch (Exception exception)
				{
					request.Fail(exception);
					// Loading failed before teardown: keep the current world. Lifecycle failures are fatal.
					if (!ReferenceEquals(session.World, request.World)) throw;
					Console.Error.WriteLine($"Scene load failed: {exception}");
				}
			}
			var world = session.World ?? throw new InvalidOperationException("No active runtime world.");
			var now = stopwatch.Elapsed;
			var delta = options.Frames > 0
				? settings.FixedDeltaTime
				: Math.Clamp((float)(now - last).TotalSeconds, 0, 0.1f);
			last = now;
			accumulator += delta;
			var windowSize = renderer.GetWindowSize();
			input.ProcessPointerInput(pointerRouter, new PointerInputContext(true, true, true, System.Numerics.Vector2.Zero,
				new(windowSize.X, windowSize.Y)));

			var steps = 0;
			while (accumulator >= settings.FixedDeltaTime && steps++ < settings.MaxPhysicsStepsPerFrame)
			{
				gameplay.PhysicsUpdate(settings.FixedDeltaTime, world);
				manager.PhysicsUpdate(settings.FixedDeltaTime, WorldTag.Game, SystemExecutionGroup.All);
				accumulator -= settings.FixedDeltaTime;
			}

			// The accumulator left over after this frame's steps is how far the frame being rendered has
			// advanced past the last one, which is what rigidbody interpolation blends by.
			rigidbodySystem.PublishFixedStepAccumulator(world, accumulator, settings.FixedDeltaTime);
			gameplay.Update(delta, world);
			manager.Update(delta, WorldTag.Game, SystemExecutionGroup.All);
			audio.Update(delta);
			manager.OnPreRender(delta, WorldTag.Game, SystemExecutionGroup.All);
			if (TryGetCamera(world, out var camera, out var transform) == false)
			{
				throw new InvalidOperationException("Active scene has no active camera.");
			}

			var config = GetRenderConfig(world);
			pipeline.PublishSnapshot([new RenderViewSubmission(view, camera, transform, config)]);
			frames++;
			frameCoordinator.PublishCompletedFrame();
			if (options.Frames > 0 && frames >= options.Frames)
			{
				running = false;
			}

			Thread.Sleep(0);
		}
	}

	private static bool TryGetCamera(World world, out Camera camera, out WorldTransform transform)
	{
		foreach (var entry in world.View<Camera, WorldTransform>())
		{
			if (!world.IsEnabled(entry.Entity))
				continue;

			camera = entry.First;
			transform = entry.Second;
			return true;
		}

		camera = default;
		transform = default;
		return false;
	}

	private static RenderConfig GetRenderConfig(World world)
	{
		foreach (var entry in world.View<WorldSettings>())
			if (entry.First.RenderConfigAsset.Asset is { } config)
				return config;

		return new RenderConfig();
	}

	private static string CurrentRid()
	{
		if (OperatingSystem.IsMacOS())
			return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
			       == System.Runtime.InteropServices.Architecture.Arm64
				? "osx-arm64"
				: "osx-x64";
		if (OperatingSystem.IsWindows())
			return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
			       == System.Runtime.InteropServices.Architecture.Arm64
				? "win-arm64"
				: "win-x64";

		throw new PlatformNotSupportedException();
	}

	private readonly record struct RuntimeOptions(string? Manifest, int Frames)
	{
		public static RuntimeOptions Parse(string[] args)
		{
			string? manifest = null;
			var frames = 0;
			for (var i = 0; i < args.Length; i++)
			{
				if (args[i] == "--manifest" && ++i < args.Length)
					manifest = Path.GetFullPath(args[i]);
				else if (args[i] == "--frames" && ++i < args.Length
				         && int.TryParse(args[i], out frames) && frames > 0)
				{
				}
				else if (args[i] == "--quit")
				{
				}
				else
					throw new ArgumentException($"Unknown or invalid argument '{args[i]}'.");
			}
			return new RuntimeOptions(manifest, frames);
		}
	}
}
