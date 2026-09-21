using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor;

/// <summary>
/// A material editor's ordinary render view. Its world contains only a lit mesh sphere and a camera;
/// the standard sky, GPU draw, G-buffer, lighting and transparent passes render it.
/// </summary>
public sealed class MaterialPreviewScene : IEditorRenderViewSource, IDisposable
{
	private static readonly Vector3 CameraPosition = new(1.4f, 1.0f, -2.7f);
	private readonly IRenderViewHost _viewHost;
	private readonly IWorldManager _worldManager;
	private readonly EditorRenderViews _views;
	private readonly EditorViewportStateBus _viewportStateBus;
	private readonly World _world = new(WorldTag.Editor);
	private readonly Entity _sphere;
	private readonly Entity _camera;
	private readonly RenderConfig _config = new() { AntiAliasing = new AntiAliasingConfig { Enabled = false } };
	private bool _disposed;

	public MaterialPreviewScene(
		IRenderViewHost viewHost,
		IWorldManager worldManager,
		EditorRenderViews views,
		EditorViewportStateBus viewportStateBus,
		Mesh sphereMesh)
	{
		_viewHost = viewHost ?? throw new ArgumentNullException(nameof(viewHost));
		_worldManager = worldManager ?? throw new ArgumentNullException(nameof(worldManager));
		_views = views ?? throw new ArgumentNullException(nameof(views));
		_viewportStateBus = viewportStateBus ?? throw new ArgumentNullException(nameof(viewportStateBus));
		ArgumentNullException.ThrowIfNull(sphereMesh);

		var light = _world.CreateEntity("Material Preview Light");
		_world.AddTransform(light, Matrix4x4.CreateFromYawPitchRoll(0.6f, 0.9f, 0.0f));
		_world.AddComponent(light, new Light
		{
			Color = ColorRGBA.White, Intensity = 1.0f, Range = 25.0f,
			Type = LightType.Directional, HorizonFade = true
		});

		_sphere = _world.CreateEntity("Material Preview Sphere", Matrix4x4.CreateScale(1.8f));
		_world.AddComponent(_sphere, new MeshRenderer { Mesh = sphereMesh });

		var camera = new Camera { ScreenResolution = new Mathematics.Int2(256, 256) };
		camera.SetPerspective(55.0f);
		_camera = _world.CreateEntity("Material Preview Camera", CreateCameraToWorld(CameraPosition, Vector3.Zero));
		_world.AddComponent(_camera, camera);

		_worldManager.RegisterWorld(_world);
		View = _viewHost.CreateView(new RenderViewDescriptor(_world, "Material Preview", RenderViewOutput.Texture));
		// TransformSystem does not run on editor worlds during Play mode, so initialize the static view now.
		SetWorld(light, _world.GetComponent<LocalTransform>(light).GetTransform());
		SetWorld(_sphere, _world.GetComponent<LocalTransform>(_sphere).GetTransform());
		SetWorld(_camera, _world.GetComponent<LocalTransform>(_camera).GetTransform());
		_views.Register(this);
	}

	public RenderViewId View { get; }
	public World World => _world;

	public void SetMaterial(Material material)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ArgumentNullException.ThrowIfNull(material);
		ref var renderer = ref _world.GetComponent<MeshRenderer>(_sphere);
		renderer.Material = material;
		// Mesh gathering visits dirty transforms; refresh the sphere's draw when live editor values change.
		_world.MarkWorldTransformChanged(_sphere);
	}

	public bool PrepareSubmission(float deltaTime, out RenderViewSubmission submission)
	{
		_ = deltaTime;
		if (_disposed || !_viewportStateBus.GetUiState(View).Visible)
		{
			submission = default;
			return false;
		}
		submission = new RenderViewSubmission(View, _world.GetComponent<Camera>(_camera),
			_world.GetComponent<WorldTransform>(_camera), _config);
		return true;
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_views.Unregister(this);
		_viewportStateBus.RemoveView(View);
		_viewHost.DestroyView(View);
		_worldManager.RemoveWorld(_world);
	}

	private void SetWorld(Entity entity, Matrix4x4 localToWorld)
	{
		Matrix4x4.Invert(localToWorld, out var worldToLocal);
		ref var transform = ref _world.GetComponent<WorldTransform>(entity);
		transform.LocalToWorld = localToWorld;
		transform.WorldToLocal = worldToLocal;
		_world.MarkWorldTransformChanged(entity);
	}

	private static Matrix4x4 CreateCameraToWorld(Vector3 position, Vector3 target)
	{
		var forward = Vector3.Normalize(target - position);
		var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
		var up = Vector3.Cross(forward, right);
		return new Matrix4x4(right.X, right.Y, right.Z, 0,
			up.X, up.Y, up.Z, 0,
			forward.X, forward.Y, forward.Z, 0,
			position.X, position.Y, position.Z, 1);
	}
}
