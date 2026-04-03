using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Gameplay;
using WolfEngine.Editor.UI;
using WolfEngine.Input;
using WolfEngine.Profiling;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Passes;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Physics;

namespace WolfEngine.Editor;

public class WolfEngineEditor
{
	private const float EditorCameraFov = 70.0f;
	private const float PhysicsFixedDeltaTime = 1.0f / 60.0f;
	private const int PhysicsMaxStepsPerFrame = 4;
	private static readonly Vector3 EditorCameraPosition = new(0.0f, 1.0f, -5.0f);

	private readonly IWorldManager _worldManager;
	private readonly IRenderPipeline _renderPipeline;
	private readonly IUiFrameProvider _uiFrameProvider;
	private readonly IRenderer _renderer;
	private readonly RenderGraph _renderGraph;
	private readonly IInputSystem _inputSystem;
	private readonly EditorViewportStateBus _viewportStateBus;
	private readonly EditorFrameCoordinator _editorFrameCoordinator;
	private readonly EditorCameraContext _cameraContext;
	private readonly List<World> _renderWorlds = new(2);
	private readonly EditorGui _editorGui;
	private readonly IEditorSceneWorkspace _sceneWorkspace;
	private readonly IEditorPlaySession _playSession;
	private readonly IGameplayAssemblyHost _gameplayAssemblyHost;
	private readonly IEditorSceneReloadService _sceneReloadService;
	private readonly IAssetInstanceRegistry _assetInstanceRegistry;
	private readonly IEditorNotificationService _notificationService;
	private readonly IEditorUndoRedoService _undoRedoService;
	private readonly IProjectTypeCatalog _typeCatalog;
	private readonly List<ISystem> _registeredGameplaySystems = new();
	private readonly FixedStepAccumulator _physicsAccumulator = new(PhysicsFixedDeltaTime, PhysicsMaxStepsPerFrame);

	private EditorScene _currentScene = null!;

	private World _editorWorld = null!;
	private Entity _editorCamera;
	private volatile bool _running;
	private long _boundGameplayGeneration;
	private World? _boundGameplayWorld;
	private IGameplayModule? _boundGameplayModule;

	public WolfEngineEditor(
		IWorldManager worldManager,
		IRenderPipeline renderPipeline,
		IUiFrameProvider uiFrameProvider,
		IRenderer renderer,
		RenderGraph renderGraph,
		IInputSystem inputSystem,
		EditorViewportStateBus viewportStateBus,
		EditorFrameCoordinator editorFrameCoordinator,
		EditorCameraContext cameraContext,
		EditorGui editorGui,
		IEditorSceneWorkspace sceneWorkspace,
		IEditorPlaySession playSession,
		IProjectTypeCatalog typeCatalog,
		IGameplayAssemblyHost gameplayAssemblyHost,
		IEditorSceneReloadService sceneReloadService,
		IAssetInstanceRegistry assetInstanceRegistry,
		IEditorNotificationService notificationService,
		IEditorUndoRedoService undoRedoService)
	{
		_worldManager = worldManager ?? throw new ArgumentNullException(nameof(worldManager));
		_renderPipeline = renderPipeline ?? throw new ArgumentNullException(nameof(renderPipeline));
		_uiFrameProvider = uiFrameProvider ?? throw new ArgumentNullException(nameof(uiFrameProvider));
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
		_renderGraph = renderGraph ?? throw new ArgumentNullException(nameof(renderGraph));
		_inputSystem = inputSystem ?? throw new ArgumentNullException(nameof(inputSystem));
		_viewportStateBus = viewportStateBus ?? throw new ArgumentNullException(nameof(viewportStateBus));
		_editorFrameCoordinator = editorFrameCoordinator ?? throw new ArgumentNullException(nameof(editorFrameCoordinator));
		_cameraContext = cameraContext ?? throw new ArgumentNullException(nameof(cameraContext));
		_editorGui = editorGui ?? throw new ArgumentNullException(nameof(editorGui));
		_sceneWorkspace = sceneWorkspace ?? throw new ArgumentNullException(nameof(sceneWorkspace));
		_playSession = playSession ?? throw new ArgumentNullException(nameof(playSession));
		_typeCatalog = typeCatalog ?? throw new ArgumentNullException(nameof(typeCatalog));
		_gameplayAssemblyHost = gameplayAssemblyHost ?? throw new ArgumentNullException(nameof(gameplayAssemblyHost));
		_sceneReloadService = sceneReloadService ?? throw new ArgumentNullException(nameof(sceneReloadService));
		_assetInstanceRegistry = assetInstanceRegistry ?? throw new ArgumentNullException(nameof(assetInstanceRegistry));
		_notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
		_undoRedoService = undoRedoService ?? throw new ArgumentNullException(nameof(undoRedoService));
	}

	public void Run()
	{
		_running = true;
		CreateWorlds();
		EditorLoop();
	}

	public void Stop()
	{
		_running = false;
	}

	private void CreateWorlds()
	{
		_editorWorld = _worldManager.CreateWorld(WorldTag.Editor);
		var authoringWorld = _worldManager.CreateWorld(WorldTag.Authoring);
		
		var authoringScene = new EditorScene
		{
			World = authoringWorld,
			EntityIcons = new()
		};
		_sceneWorkspace.Initialize(authoringScene);

		_worldManager.AddSystem<CameraResolutionUpdater>();
		_worldManager.AddSystem<TransformSystem>();
		_worldManager.AddSystem(new RigidbodySystem(), SystemExecutionGroup.Gameplay);
		_worldManager.AddSystem(new CameraMoverSystem(_inputSystem, _viewportStateBus));
		
		var sun = authoringWorld.CreateEntity("Sun");
		var light = new Light
		{
			Color = ColorRGBA.White, Intensity = 1, Range = 25.0f, Type = LightType.Directional, HorizonFade = true
		};
		authoringWorld.AddTransform(sun, Matrix4x4.Identity);
		authoringWorld.AddComponent(sun, light);
		
		authoringScene.EntityIcons.Add(sun, "light");

		_editorCamera = CreateEditorCamera(_editorWorld);
		_currentScene = _playSession.ActiveScene;
		RefreshRenderWorlds();
	}

	private void EditorLoop()
	{
		var stopwatch = Stopwatch.StartNew();
		var last = stopwatch.Elapsed;

		while (_running)
		{
			var frameStart = stopwatch.Elapsed;
			FrameProfiler.Instance.BeginFrame("Editor Frame");

			var deltaTime = (float)(frameStart - last).TotalSeconds;
			last = frameStart;

			HandleGameplayBuildAndReload();
			SyncCurrentScene();
			ValidateSelection();
			EnsureGameplayModuleBound();

			using (FrameProfiler.Instance.Measure("World Update"))
			{
				UpdatePhysics(deltaTime);
				UpdateGameplay(deltaTime);
				var (worldMask, groupMask) = GetExecutionMask();
				_worldManager.Update(deltaTime, worldMask, groupMask);
			}

			using (FrameProfiler.Instance.Measure("Pre-Render"))
			{
				var (worldMask, groupMask) = GetExecutionMask();
				_worldManager.OnPreRender(deltaTime, worldMask, groupMask);
			}

			using (FrameProfiler.Instance.Measure("Publish Snapshot"))
			{
				PublishSnapshot();
			}

			using (FrameProfiler.Instance.Measure("UI"))
			{
				_uiFrameProvider.NewFrame(deltaTime, _renderer.GetWindowSize(), _renderGraph.GetFrameBufferSize());
				_uiFrameProvider.RunGui(() =>
				{
					_editorGui.Draw(_currentScene);
				});
				_editorFrameCoordinator.PublishCompletedFrame();
			}

			FrameProfiler.Instance.EndFrame();
			ApplyEditorFrameCap(stopwatch, frameStart);
		}
	}

	private static void ApplyEditorFrameCap(Stopwatch stopwatch, TimeSpan frameStart)
	{
		if (EditorPreferences.GetLimitFPS() == false)
		{
			Thread.Sleep(0);
			return;
		}

		var maxFPS = EditorPreferences.GetMaxFPS();
		if (maxFPS <= 0)
		{
			Thread.Sleep(0);
			return;
		}

		var targetFrameDuration = TimeSpan.FromSeconds(1.0 / maxFPS);
		while (true)
		{
			var elapsed = stopwatch.Elapsed - frameStart;
			var remaining = targetFrameDuration - elapsed;
			if (remaining <= TimeSpan.Zero)
			{
				break;
			}

			if (remaining > TimeSpan.FromMilliseconds(2))
			{
				Thread.Sleep(1);
				continue;
			}

			Thread.Sleep(0);
		}
	}

	private void PublishSnapshot()
	{
		ref var camera = ref _editorWorld.GetComponent<Camera>(_editorCamera);
		var viewportRenderState = _viewportStateBus.GetRenderState();
		var renderSize = viewportRenderState.RenderSizePixels;
		if (renderSize.X > 0 && renderSize.Y > 0 && camera.ScreenResolution != renderSize)
		{
			camera.ScreenResolution = renderSize;
			camera.SetPerspective(camera.Fov);
		}

		ref var cameraWorldTransform = ref _editorWorld.GetComponent<WorldTransform>(_editorCamera);
		_cameraContext.Publish(camera, cameraWorldTransform);
		_renderPipeline.PublishSnapshot(camera, cameraWorldTransform, GetConfig(), _renderWorlds);
	}

	private void SyncCurrentScene()
	{
		var nextScene = _playSession.ActiveScene;
		if (ReferenceEquals(_currentScene, nextScene))
		{
			return;
		}

		var selectedEntityId = TryGetSelectedEntityId(_currentScene);
		_currentScene = nextScene;
		RefreshRenderWorlds();
		RestoreSelectedEntity(_currentScene, selectedEntityId);
	}

	private void UpdateGameplay(float deltaTime)
	{
		if (_playSession.State != EditorPlayState.Playing ||
		    _playSession.RuntimeScene is not { } runtimeScene ||
		    ReferenceEquals(_boundGameplayWorld, runtimeScene.World) == false)
		{
			return;
		}

		_boundGameplayModule?.Update(deltaTime, runtimeScene.World);
	}

	private void UpdatePhysics(float deltaTime)
	{
		if (CanAdvancePhysics(_playSession.State, _playSession.RuntimeScene?.World, _boundGameplayWorld) == false ||
		    _playSession.RuntimeScene is not { } runtimeScene)
		{
			_physicsAccumulator.Reset();
			return;
		}

		_physicsAccumulator.Execute(deltaTime, fixedDeltaTime =>
		{
			_boundGameplayModule?.PhysicsUpdate(fixedDeltaTime, runtimeScene.World);
			_worldManager.PhysicsUpdate(fixedDeltaTime, WorldTag.Game, SystemExecutionGroup.All);
		});
	}

	internal static bool CanAdvancePhysics(EditorPlayState state, World? runtimeWorld, World? boundGameplayWorld)
	{
		return state == EditorPlayState.Playing &&
		       runtimeWorld is not null &&
		       ReferenceEquals(runtimeWorld, boundGameplayWorld);
	}

	private void EnsureGameplayModuleBound()
	{
		if (_playSession.RuntimeScene is not { } runtimeScene)
		{
			UnbindGameplayModule();
			return;
		}

		var loadResult = _gameplayAssemblyHost.EnsureLoaded();
		if (loadResult.Generation == 0)
		{
			UnbindGameplayModule();
			return;
		}

		if (loadResult.Generation == _boundGameplayGeneration &&
		    ReferenceEquals(_boundGameplayWorld, runtimeScene.World))
		{
			return;
		}

		UnbindGameplayModule();
		RegisterGameplaySystems(loadResult.Module?.CreateSystems());
		loadResult.Module?.OnLoaded(runtimeScene.World);
		_boundGameplayGeneration = loadResult.Generation;
		_boundGameplayWorld = runtimeScene.World;
		_boundGameplayModule = loadResult.Module;
	}

	private void HandleGameplayBuildAndReload()
	{
		if (_gameplayAssemblyHost.TryConsumeBuildResult(out var buildResult) == false)
		{
			return;
		}

		if (buildResult.Succeeded == false)
		{
			_notificationService.ReportError(string.IsNullOrWhiteSpace(buildResult.Output)
				? "Gameplay build failed."
				: buildResult.Output);
			return;
		}

		try
		{
			ApplyGameplayReload(buildResult);
		}
		catch (Exception exception)
		{
			_notificationService.ReportError($"Gameplay reload failed:{Environment.NewLine}{exception}");
		}
	}

	private void ApplyGameplayReload(GameplayBuildResult buildResult)
	{
		var previousState = _playSession.State;
		var selectedEntityId = TryGetSelectedEntityId(_playSession.ActiveScene);
		var snapshot = _sceneReloadService.Capture(_playSession.AuthoringScene);

		UnbindGameplayModule();
		_playSession.Stop();
		_editorGui.PrepareForGameplayReload();
		_assetInstanceRegistry.ClearCachedInstances();
		_typeCatalog.ClearCaches();
		RuntimeComponentAccessor.ClearCachedDelegates();
		RuntimeComponentFieldEditor.ClearCachedFields();
		RuntimeAssetDescriptor.ClearCache();
		ProjectTypeResolverUtility.ClearCaches();

		_gameplayAssemblyHost.ApplyPreparedBuild(buildResult);
		var restoredScene = _sceneReloadService.Restore(snapshot, WorldTag.Authoring);
		_undoRedoService.Clear();
		_sceneWorkspace.ReplaceCurrentScene(restoredScene);
		_playSession.Restart(previousState);
		_currentScene = _playSession.ActiveScene;
		RefreshRenderWorlds();
		RestoreSelectedEntity(_currentScene, selectedEntityId);
	}

	private Guid? TryGetSelectedEntityId(EditorScene scene)
	{
		return EditorGui.HasSelectedEntity &&
			       scene.World.IsAlive(EditorGui.SelectedEntity) &&
			       scene.EntityIds.TryGetValue(EditorGui.SelectedEntity, out var selectedEntityId) &&
			       selectedEntityId != Guid.Empty
			? selectedEntityId
			: null;
	}

	private void RestoreSelectedEntity(EditorScene scene, Guid? selectedEntityId)
	{
		if (selectedEntityId is not { } entityId)
		{
			EditorGui.ClearEntitySelection();
			return;
		}

		foreach (var entry in scene.EntityIds)
		{
			if (entry.Value == entityId)
			{
				EditorGui.SelectEntity(entry.Key, scene.World);
				return;
			}
		}

		EditorGui.ClearEntitySelection();
	}

	private RenderConfig GetConfig()
	{
		RenderConfig config = null;
		foreach (var entry in _currentScene.World.View<WorldSettings>())
		{
			config = entry.First.RenderConfigAsset.Asset;
		}

		return config ?? new RenderConfig();
	}

	private void ValidateSelection()
	{
		if (EditorGui.HasSelectedEntity == false)
		{
			return;
		}

		if (_currentScene.World.IsAlive(EditorGui.SelectedEntity))
		{
			return;
		}

		EditorGui.ClearEntitySelection();
	}

	private void RefreshRenderWorlds()
	{
		_renderWorlds.Clear();
		_renderWorlds.Add(_editorWorld);
		_renderWorlds.Add(_currentScene.World);
	}

	private (WorldTag WorldMask, SystemExecutionGroup GroupMask) GetExecutionMask()
	{
		return _playSession.State switch
		{
			EditorPlayState.Edit => (WorldTag.Editor | WorldTag.Authoring, SystemExecutionGroup.Shared),
			EditorPlayState.Playing => (WorldTag.Editor | WorldTag.Game, SystemExecutionGroup.All),
			EditorPlayState.Paused => (WorldTag.Editor | WorldTag.Game, SystemExecutionGroup.Shared),
			_ => (WorldTag.Editor | WorldTag.Authoring, SystemExecutionGroup.Shared)
		};
	}

	private void RegisterGameplaySystems(IEnumerable<ISystem>? systems)
	{
		if (systems is null)
		{
			return;
		}

		foreach (var system in systems)
		{
			if (system is null)
			{
				continue;
			}

			_worldManager.AddSystem(system, SystemExecutionGroup.Gameplay);
			_registeredGameplaySystems.Add(system);
		}
	}

	private void UnbindGameplayModule()
	{
		if (_boundGameplayWorld is null)
		{
			return;
		}

		_boundGameplayModule?.OnUnloading(_boundGameplayWorld);
		for (var index = _registeredGameplaySystems.Count - 1; index >= 0; index--)
		{
			_worldManager.RemoveSystem(_registeredGameplaySystems[index]);
		}

		_registeredGameplaySystems.Clear();
		_boundGameplayWorld = null;
		_boundGameplayModule = null;
		_boundGameplayGeneration = 0;
	}

	private static Entity CreateEditorCamera(World world)
	{
		var camera = new Camera
		{
			ScreenResolution = Screen.CurrentResolution,
			AutoResolution = false
		};
		camera.SetPerspective(EditorCameraFov);

		var target = Vector3.Zero;
		var up = Vector3.UnitY;
		var view = CreateLookAtLeftHanded(EditorCameraPosition, target, up);
		Matrix4x4.Invert(view, out var worldTransform);

		var entity = world.CreateEntity("Editor Camera", worldTransform);
		world.AddComponent(entity, camera);
		world.AddComponent(entity, new CameraMover());
		return entity;
	}

	private static Matrix4x4 CreateLookAtLeftHanded(Vector3 position, Vector3 target, Vector3 up)
	{
		var zAxis = Vector3.Normalize(target - position);
		var xAxis = Vector3.Normalize(Vector3.Cross(up, zAxis));
		var yAxis = Vector3.Cross(zAxis, xAxis);

		return new Matrix4x4(
			xAxis.X, yAxis.X, zAxis.X, 0.0f,
			xAxis.Y, yAxis.Y, zAxis.Y, 0.0f,
			xAxis.Z, yAxis.Z, zAxis.Z, 0.0f,
			-Vector3.Dot(xAxis, position),
			-Vector3.Dot(yAxis, position),
			-Vector3.Dot(zAxis, position),
			1.0f);
	}
}
