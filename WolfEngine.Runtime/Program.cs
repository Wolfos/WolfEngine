using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
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
		services.AddSingleton(new RenderPresentationOptions { OutputMode = RenderOutputMode.FullWindow });
		var packagedShaders = new PackagedShaderProvider(catalog);
		services.AddSingleton<IShaderProvider>(packagedShaders);
		services.AddSingleton<IPackagedShaderProvider>(packagedShaders);

		var nullUi = new RuntimeNullUi();
		services.AddSingleton<IUiFrameProvider>(nullUi);
		services.AddSingleton<IImGuiInputSink>(nullUi);
		services.AddSingleton<IImGuiRenderer>(NullImGuiRenderer.Instance);
		services.AddSingleton(catalog);
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
		worldManager.RegisterWorld(world);
		worldManager.AddSystem<CameraResolutionUpdater>();
		worldManager.AddSystem<TransformSystem>();
		worldManager.AddSystem(new VehicleSystem(), SystemExecutionGroup.Gameplay);
		worldManager.AddSystem(provider.GetRequiredService<RigidbodySystem>(), SystemExecutionGroup.Gameplay);
		foreach (var system in gameplay.CreateSystems(provider))
			worldManager.AddSystem(system, SystemExecutionGroup.Gameplay);

		gameplay.OnLoaded(world);
		var renderPipeline = provider.GetRequiredService<IRenderPipeline>();
		var running = true;
		Exception? gameError = null;
		
		var gameThread = new Thread(() =>
		{
			try
			{
				GameLoop(provider, world, gameplay, settings, options, ref running);
			}
			catch (Exception exception)
			{
				gameError = exception;
				Console.Error.WriteLine($"runtime game loop failed: {exception}");
				provider.GetRequiredService<EditorFrameCoordinator>().RequestShutdown();
				renderer.RequestShutdown();
			}
		})
		{
			IsBackground = true,
			Name = "GameThread"
		};
		gameThread.Start();
		
		renderPipeline.Run(() =>
		{
			Console.WriteLine("runtime renderer ready");
			
		});

		running = false;
		gameThread?.Join();
		gameplay.OnUnloading(world);
		AssetDatabase.ClearInstanceRegistry();
		if (gameError is not null)
			throw gameError;

		return 0;
	}

	private static void GameLoop(
		IServiceProvider services,
		World world,
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
		var stopwatch = Stopwatch.StartNew();
		var last = stopwatch.Elapsed;
		var accumulator = 0f;
		var frames = 0;
		Task<FrameCapture>? capture = null;
		while (running)
		{
			var now = stopwatch.Elapsed;
			var delta = options.Frames > 0
				? settings.FixedDeltaTime
				: Math.Clamp((float)(now - last).TotalSeconds, 0, 0.1f);
			last = now;
			accumulator += delta;

			var steps = 0;
			while (accumulator >= settings.FixedDeltaTime && steps++ < settings.MaxPhysicsStepsPerFrame)
			{
				gameplay.PhysicsUpdate(settings.FixedDeltaTime, world);
				manager.PhysicsUpdate(settings.FixedDeltaTime, WorldTag.Game, SystemExecutionGroup.All);
				accumulator -= settings.FixedDeltaTime;
			}

			gameplay.Update(delta, world);
			manager.Update(delta, WorldTag.Game, SystemExecutionGroup.All);
			audio.Update(delta);
			manager.OnPreRender(delta, WorldTag.Game, SystemExecutionGroup.All);
			if (TryGetCamera(world, out var camera, out var transform) == false)
			{
				throw new InvalidOperationException("Initial scene has no active camera.");
			}

			var config = GetRenderConfig(world);
			pipeline.PublishSnapshot(camera, transform, config, [world]);
			frames++;
			frameCoordinator.PublishCompletedFrame();
			if (options.Frames > 0 && frames >= options.Frames && capture is null)
				capture = renderer.CaptureNextFrameAsync();
			if (capture is { IsCompleted: true })
			{
				var imageData = capture.GetAwaiter().GetResult();
				if (options.Capture is not null)
				{
					Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Capture))!);
					using var image = Image.LoadPixelData<Rgba32>(imageData.Rgba8, imageData.Width, imageData.Height);
					image.SaveAsPng(options.Capture);
				}

				renderer.RequestShutdown();
				running = false;
			}

			Thread.Sleep(0);
		}

		frameCoordinator.RequestShutdown();
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

	private readonly record struct RuntimeOptions(string? Manifest, int Frames, string? Capture)
	{
		public static RuntimeOptions Parse(string[] args)
		{
			string? manifest = null;
			string? capture = null;
			var frames = 0;
			for (var i = 0; i < args.Length; i++)
			{
				if (args[i] == "--manifest" && ++i < args.Length)
					manifest = Path.GetFullPath(args[i]);
				else if (args[i] == "--frames" && ++i < args.Length
				         && int.TryParse(args[i], out frames) && frames > 0)
				{
				}
				else if (args[i] == "--capture" && ++i < args.Length)
					capture = Path.GetFullPath(args[i]);
				else if (args[i] == "--quit")
				{
				}
				else
					throw new ArgumentException($"Unknown or invalid argument '{args[i]}'.");
			}

			if (capture is not null && frames == 0)
				throw new ArgumentException("--capture requires --frames.");

			return new RuntimeOptions(manifest, frames, capture);
		}
	}
}
