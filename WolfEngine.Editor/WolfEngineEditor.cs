using System.Diagnostics;
using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Editor.UI;
using WolfEngine.Input;
using WolfEngine.Profiling;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Editor;

public class WolfEngineEditor
{
	private const float EditorCameraFov = 70.0f;
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

	private EditorScene _currentScene;

	private World _editorWorld = null!;
	private World _gameWorld = null!;
	private Entity _editorCamera;
	private volatile bool _running;

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
		EditorGui editorGui)
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
		_editorGui = editorGui;
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
		_gameWorld = _worldManager.CreateWorld(WorldTag.Game);
		
		_currentScene = new EditorScene
		{
			World = _gameWorld,
			EntityIcons = new()
		};

		_worldManager.AddSystem<CameraResolutionUpdater>();
		_worldManager.AddSystem<TransformSystem>();
		_worldManager.AddSystem(new CameraMoverSystem(_inputSystem, _viewportStateBus));
		
		var sun = _gameWorld.CreateEntity("Sun");
		var light = new Light
		{
			Color = ColorRGBA.White, Intensity = 1, Type = LightType.Directional, HorizonFade = true
		};
		_gameWorld.AddTransform(sun, Matrix4x4.Identity);
		_gameWorld.AddComponent(sun, light);
		
		_currentScene.EntityIcons.Add(sun, "light");

		_editorCamera = CreateEditorCamera(_editorWorld);

		_renderWorlds.Clear();
		_renderWorlds.Add(_editorWorld);
		_renderWorlds.Add(_gameWorld);
	}

	private void EditorLoop()
	{
		var stopwatch = Stopwatch.StartNew();
		var last = stopwatch.Elapsed;

		while (_running)
		{
			FrameProfiler.Instance.BeginFrame("Editor Frame");

			var now = stopwatch.Elapsed;
			var deltaTime = (float)(now - last).TotalSeconds;
			last = now;

			using (FrameProfiler.Instance.Measure("World Update"))
			{
				_worldManager.Update(deltaTime, WorldTag.All);
			}

			using (FrameProfiler.Instance.Measure("Pre-Render"))
			{
				_worldManager.OnPreRender(deltaTime, WorldTag.All);
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

	private RenderConfig GetConfig()
	{
		RenderConfig config = null;
		foreach (var entry in _gameWorld.View<WorldSettings>())
		{
			config = entry.First.RenderConfigAsset.Asset;
		}

		return config ?? new RenderConfig();
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
