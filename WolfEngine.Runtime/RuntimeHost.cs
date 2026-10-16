using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.Audio;
using WolfEngine.ECS;
using WolfEngine.Gameplay;
using WolfEngine.Input;
using WolfEngine.Mathematics;
using WolfEngine.Physics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.Shaders;
using WolfEngine.Rendering.UI;
using WolfEngine.Serialization;

namespace WolfEngine.Runtime;

/// <summary>What a platform host supplies to run a cooked game.</summary>
public sealed class RuntimeHostOptions
{
	/// <summary>The mounted packs of a cooked game. The host keeps ownership and disposes it after the runtime host.</summary>
	public required WolfPackCatalog Catalog { get; init; }

	/// <summary>
	/// The game's gameplay module. How it is obtained is up to the platform: the desktop host loads the gameplay
	/// assembly from the packs, while a NativeAOT host has it compiled in.
	/// </summary>
	public required IGameplayModule Gameplay { get; init; }

	/// <summary>Registers platform services, such as gameplay UI, after the engine's own.</summary>
	public Action<IServiceCollection>? ConfigureServices { get; init; }

	/// <summary>
	/// Assemblies besides the engine's whose component and data asset serializers must be registered before the
	/// first scene loads, such as the gameplay assembly and the UI assembly.
	/// </summary>
	public IReadOnlyList<Assembly> ComponentAssemblies { get; init; } = [];

	/// <summary>
	/// For automated runs: step every frame by the fixed timestep and finish after this many frames. Zero runs in
	/// real time until the host stops.
	/// </summary>
	public int FrameCount { get; init; }

	/// <summary>
	/// Whether to simulate physics. Platforms without a physics library build, such as iOS until Jolt is built for
	/// it, turn this off; rigidbodies, colliders and vehicles then stay where they were placed.
	/// </summary>
	public bool EnablePhysics { get; init; } = true;
}

/// <summary>
/// Runs a cooked game: builds the engine's services, loads the initial scene and steps gameplay one frame at a
/// time. Platform hosts own the threads and the render loop, and call <see cref="Tick"/> once per game frame.
/// </summary>
public sealed class RuntimeHost : IDisposable
{
	private readonly ServiceProvider _services;
	private readonly IGameplayModule _gameplay;
	private readonly GameplayWorldSession _session;
	private readonly RenderViewId _primaryView;
	private readonly World _initialWorld;
	private readonly int _frameCount;
	private readonly int _memoryReportFrame = ReadMemoryReportFrame();
	private readonly IWorldManager _worldManager;
	private readonly IRenderPipeline _renderPipeline;
	private readonly IRenderer _renderer;
	private readonly EditorFrameCoordinator _frameCoordinator;
	private readonly IAudioRuntime _audio;
	private readonly RigidbodySystem? _rigidbodySystem;
	private readonly IInputSystem _input;
	private readonly IPointerInputRouter _pointerRouter;
	private readonly SceneLoadRequests _sceneRequests;
	private readonly IRuntimeSceneLoader _sceneLoader;
	private readonly Stopwatch _clock = new();
	private TimeSpan _lastFrameTime;
	private float _accumulator;
	private int _frames;
	private bool _started;

	private RuntimeHost(RuntimeHostOptions options, CookedRuntimeSettings settings, ServiceProvider services)
	{
		Settings = settings;
		_services = services;
		_gameplay = options.Gameplay;
		_frameCount = options.FrameCount;
		_worldManager = services.GetRequiredService<IWorldManager>();
		_renderPipeline = services.GetRequiredService<IRenderPipeline>();
		_renderer = services.GetRequiredService<IRenderer>();
		_frameCoordinator = services.GetRequiredService<EditorFrameCoordinator>();
		_audio = services.GetRequiredService<IAudioRuntime>();
		_rigidbodySystem = options.EnablePhysics ? services.GetRequiredService<RigidbodySystem>() : null;
		_input = services.GetRequiredService<IInputSystem>();
		_pointerRouter = services.GetRequiredService<IPointerInputRouter>();
		_sceneRequests = services.GetRequiredService<SceneLoadRequests>();
		_sceneLoader = services.GetRequiredService<IRuntimeSceneLoader>();

		_renderer.SetWindowSize(new Int2(settings.Width, settings.Height));
		AssetDatabase.SetInstanceRegistry(services.GetRequiredService<IAssetInstanceRegistry>());

		var initialSceneId = options.Catalog.Manifest.InitialSceneId;
		_initialWorld = _sceneLoader.Load(initialSceneId);
		Console.WriteLine($"runtime loaded scene {initialSceneId:D}");

		_worldManager.AddSystem<CameraResolutionUpdater>();
		_worldManager.AddSystem(new AnimationRenderResourceLifecycle(services.GetRequiredService<RenderGraph>()));
		// Before TransformSystem, so exposed bone sockets propagate in the frame they are posed.
		_worldManager.AddSystem<AnimationSystem>();
		if (_rigidbodySystem is not null)
		{
			// Register before TransformSystem so interpolated child poses propagate this frame.
			_worldManager.AddSystem(new VehicleSystem(), SystemExecutionGroup.Gameplay);
			_worldManager.AddSystem(_rigidbodySystem, SystemExecutionGroup.Gameplay);
		}
		else
		{
			Console.WriteLine("runtime physics disabled");
		}

		_worldManager.AddSystem<TransformSystem>();

		var renderGraph = services.GetRequiredService<RenderGraph>();
		_primaryView = renderGraph.CreateView(new RenderViewDescriptor(_initialWorld, "game", RenderViewOutput.Backbuffer));
		_session = new GameplayWorldSession(
			_worldManager,
			_gameplay,
			services,
			next =>
			{
				if (renderGraph.RebindView(_primaryView, next) == false)
				{
					throw new InvalidOperationException("Runtime render view is unavailable.");
				}
			},
			_audio.StopAll);
	}

	public CookedRuntimeSettings Settings { get; }

	public IServiceProvider Services => _services;

	/// <summary>The engine's render pipeline, which the platform host drives.</summary>
	public IRenderPipeline RenderPipeline => _renderPipeline;

	/// <summary>
	/// Mounts the game's services and loads its initial scene. The scene starts running on the first
	/// <see cref="Tick"/>, on whichever thread the host steps gameplay from.
	/// </summary>
	public static RuntimeHost Create(RuntimeHostOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		var catalog = options.Catalog;
		Console.WriteLine($"runtime mounted {catalog.Manifest.Packs.Count} cooked packs");
		RuntimeTarget.EnsureCanRun(catalog.Manifest.Target);

		var settings = JsonSerializer.Deserialize(
			               catalog.Read(catalog.Manifest.RuntimeSettingsId),
			               EngineJsonContext.Default.CookedRuntimeSettings)
		               ?? throw new InvalidDataException("Runtime settings are invalid.");

		RegisterSerializers(options.ComponentAssemblies);

		var services = new ServiceCollection();
		WolfEngine.ConfigureServices(services);
		AddRuntimeServices(services, catalog);
		options.ConfigureServices?.Invoke(services);

		var provider = services.BuildServiceProvider();
		try
		{
			return new RuntimeHost(options, settings, provider);
		}
		catch
		{
			AssetDatabase.ClearInstanceRegistry();
			provider.Dispose();
			throw;
		}
	}

	/// <summary>
	/// Steps gameplay by one frame: scene load requests, fixed physics steps, gameplay and system updates, then
	/// publishes a render snapshot. Returns false once a fixed <see cref="RuntimeHostOptions.FrameCount"/> has run.
	/// </summary>
	public bool Tick()
	{
		if (_started == false)
		{
			_session.Replace(_initialWorld);
			_clock.Start();
			_lastFrameTime = _clock.Elapsed;
			_started = true;
		}

		ProcessSceneLoadRequest();

		var world = _session.World ?? throw new InvalidOperationException("No active runtime world.");
		var now = _clock.Elapsed;
		var delta = _frameCount > 0
			? Settings.FixedDeltaTime
			: Math.Clamp((float)(now - _lastFrameTime).TotalSeconds, 0, 0.1f);
		_lastFrameTime = now;
		_accumulator += delta;

		var windowSize = _renderer.GetWindowSize();
		_input.ProcessPointerInput(
			_pointerRouter,
			new PointerInputContext(true, true, true, System.Numerics.Vector2.Zero, new(windowSize.X, windowSize.Y)));

		var steps = 0;
		while (_accumulator >= Settings.FixedDeltaTime && steps++ < Settings.MaxPhysicsStepsPerFrame)
		{
			_gameplay.PhysicsUpdate(Settings.FixedDeltaTime, world);
			_worldManager.PhysicsUpdate(Settings.FixedDeltaTime, WorldTag.Game, SystemExecutionGroup.All);
			_accumulator -= Settings.FixedDeltaTime;
		}

		// The accumulator left over after this frame's steps is how far the frame being rendered has
		// advanced past the last one, which is what rigidbody interpolation blends by.
		_rigidbodySystem?.PublishFixedStepAccumulator(world, _accumulator, Settings.FixedDeltaTime);
		_gameplay.Update(delta, world);
		_worldManager.Update(delta, WorldTag.Game, SystemExecutionGroup.All);
		_audio.Update(delta);
		_worldManager.OnPreRender(delta, WorldTag.Game, SystemExecutionGroup.All);
		if (TryGetCamera(world, out var camera, out var transform) == false)
		{
			throw new InvalidOperationException("Active scene has no active camera.");
		}

		var config = GetRenderConfig(world);
		_renderPipeline.PublishSnapshot([new RenderViewSubmission(_primaryView, camera, transform, config)]);
		_frames++;
		_frameCoordinator.PublishCompletedFrame();
		if (_frames == _memoryReportFrame)
		{
			PrintMemoryReport();
		}

		return _frameCount <= 0 || _frames < _frameCount;
	}

	/// <summary>
	/// Unloads the running scene, cancels pending scene loads and asks the renderer to finish. Call it from the
	/// thread that calls <see cref="Tick"/>, once gameplay has stopped.
	/// </summary>
	public void Stop()
	{
		try
		{
			_session.Stop();
		}
		finally
		{
			_sceneRequests.CancelPending();
			_frameCoordinator.RequestShutdown();
			_renderer.RequestShutdown();
		}
	}

	public void Dispose()
	{
		AssetDatabase.ClearInstanceRegistry();
		_services.Dispose();
	}

	/// <summary>Diagnostic: <c>WOLF_MEMORY_REPORT_FRAME=N</c> prints where memory goes once N frames have run.</summary>
	private static int ReadMemoryReportFrame()
	{
		return int.TryParse(Environment.GetEnvironmentVariable("WOLF_MEMORY_REPORT_FRAME"), out var frame) ? frame : 0;
	}

	private void PrintMemoryReport()
	{
		Console.WriteLine($"memory report at frame {_frames}:");
		Console.WriteLine(EngineMemoryReport.Format(_renderer));
		Console.WriteLine(_services.GetRequiredService<RenderGraphResourceRegistry>().TransientMemory);
		Console.WriteLine(_services.GetRequiredService<RuntimeAssetStore>().DescribeCpuMemory());
		if (_services.GetService<IPackagedShaderProvider>() is PackagedShaderProvider shaders)
		{
			Console.WriteLine(shaders.DescribeMemory());
		}
	}

	/// <summary>
	/// Every assembly that declares components registers its serializers the first time it is used, which can be
	/// after the first scene is read, so they are all registered up front.
	/// </summary>
	private static void RegisterSerializers(IReadOnlyList<Assembly> componentAssemblies)
	{
		SerializedTypeRegistry.EnsureRegistered(typeof(World).Assembly);
		SerializedTypeRegistry.EnsureRegistered(typeof(Camera).Assembly);
		SerializedTypeRegistry.EnsureRegistered(typeof(RigidbodySystem).Assembly);
		SerializedTypeRegistry.EnsureRegistered(typeof(AudioService).Assembly);
		foreach (var assembly in componentAssemblies)
		{
			SerializedTypeRegistry.EnsureRegistered(assembly);
		}
	}

	private static void AddRuntimeServices(IServiceCollection services, WolfPackCatalog catalog)
	{
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
	}

	private void ProcessSceneLoadRequest()
	{
		if (_sceneRequests.TryTake(_session.World, out var request) == false)
		{
			return;
		}

		try
		{
			var next = _sceneLoader.Load(request.SceneId);
			if (TryGetCamera(next, out _, out _) == false)
			{
				throw new InvalidOperationException("Scene has no active camera.");
			}

			_session.Replace(next);
			_accumulator = 0;
			_lastFrameTime = _clock.Elapsed;
			request.Complete();
		}
		catch (Exception exception)
		{
			request.Fail(exception);

			// Loading failed before teardown: keep the current world. Lifecycle failures are fatal.
			if (ReferenceEquals(_session.World, request.World) == false)
			{
				throw;
			}

			Console.Error.WriteLine($"Scene load failed: {exception}");
		}
	}

	private static bool TryGetCamera(World world, out Camera camera, out WorldTransform transform)
	{
		foreach (var entry in world.View<Camera, WorldTransform>())
		{
			if (world.IsEnabled(entry.Entity) == false)
			{
				continue;
			}

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
		{
			if (entry.First.RenderConfigAsset.Asset is { } config)
			{
				return config;
			}
		}

		return new RenderConfig();
	}
}
