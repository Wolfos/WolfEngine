using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Editor;

/// <summary>
/// A small world with its own render view: a light, a camera and a spinning primitive. It is the simplest thing that
/// exercises a second view end to end — its own world, camera, projection, draws and output texture — and it is what
/// the preview window shows and what automation captures to check that views stay independent.
/// </summary>
/// <remarks>
/// The world has no hierarchy. The scene writes world transforms from local state so it also renders when
/// Play mode excludes editor worlds from their normal transform-system update. The camera's local state is
/// driven by <see cref="EditorCameraSystem"/> when editor worlds are updating.
/// </remarks>
public sealed class EditorPreviewScene : IEditorRenderViewSource, IDisposable
{
	private static readonly Vector3 CameraPosition = new(2.6f, 1.8f, -3.4f);
	private const float SpinRadiansPerSecond = 0.8f;

	private readonly IRenderViewHost _viewHost;
	private readonly IWorldManager _worldManager;
	private readonly EditorRenderViews _views;
	private readonly World _world = new(WorldTag.Editor);
	private readonly Entity _camera;
	private readonly Entity _box;
	private readonly Entity _sphere;
	private readonly Entity _light;
	private readonly RenderConfig _config = new() { AntiAliasing = new AntiAliasingConfig { Enabled = false } };
	private float _spin;
	private bool _disposed;

	public EditorPreviewScene(IRenderViewHost viewHost, IWorldManager worldManager, EditorRenderViews views, string name)
	{
		_viewHost = viewHost ?? throw new ArgumentNullException(nameof(viewHost));
		_worldManager = worldManager ?? throw new ArgumentNullException(nameof(worldManager));
		_views = views ?? throw new ArgumentNullException(nameof(views));

		_light = _world.CreateEntity("Preview Light");
		_world.AddTransform(_light, Matrix4x4.CreateFromYawPitchRoll(0.6f, 0.9f, 0.0f));
		_world.AddComponent(_light, new Light
		{
			Color = ColorRGBA.White, Intensity = 1, Range = 25.0f, Type = LightType.Directional, HorizonFade = true
		});

		_box = CreatePrimitive("Preview Box", DebugPrimitiveType.Box, new ColorRGBA(0.9f, 0.45f, 0.1f, 1.0f));
		_sphere = CreatePrimitive("Preview Sphere", DebugPrimitiveType.Sphere, new ColorRGBA(0.2f, 0.55f, 0.95f, 1.0f));

		var camera = new Camera { ScreenResolution = new Mathematics.Int2(16, 9) };
		camera.SetPerspective(60.0f);
		_camera = _world.CreateEntity("Preview Camera", CreateCameraToWorld(CameraPosition, Vector3.Zero, Vector3.UnitY));
		_world.AddComponent(_camera, camera);

		_worldManager.RegisterWorld(_world);
		View = _viewHost.CreateView(new RenderViewDescriptor(_world, name, RenderViewOutput.Texture));
		_world.AddComponent(_camera, new EditorCameraMover { View = View });
		WriteTransforms();
		_views.Register(this);
	}

	public RenderViewId View { get; }

	public World World => _world;

	public bool PrepareSubmission(float deltaTime, out RenderViewSubmission submission)
	{
		if (_disposed)
		{
			submission = default;
			return false;
		}

		_spin += SpinRadiansPerSecond * Math.Max(deltaTime, 0.0f);
		WriteTransforms();
		submission = new RenderViewSubmission(
			View,
			_world.GetComponent<Camera>(_camera),
			_world.GetComponent<WorldTransform>(_camera),
			_config);
		return true;
	}

	/// <summary>Stops the primitives spinning, so repeated captures of the view are comparable.</summary>
	public bool Frozen { get; set; }

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_views.Unregister(this);
		_viewHost.DestroyView(View);
		_worldManager.RemoveWorld(_world);
	}

	private Entity CreatePrimitive(string name, DebugPrimitiveType type, ColorRGBA tint)
	{
		var entity = _world.CreateEntity(name);
		_world.AddTransform(entity, Matrix4x4.Identity);
		_world.AddComponent(entity, new DebugPrimitiveRenderer { PrimitiveType = type, Tint = tint, AlphaMode = AlphaMode.Opaque });
		return entity;
	}

	private void WriteTransforms()
	{
		var spin = Frozen ? 0.0f : _spin;
		SetWorld(_box, Matrix4x4.CreateRotationY(spin) * Matrix4x4.CreateTranslation(-0.8f, 0.0f, 0.0f));
		SetWorld(_sphere, Matrix4x4.CreateScale(0.9f) * Matrix4x4.CreateTranslation(0.9f, 0.0f, 0.4f));
		SetWorld(_light, Matrix4x4.CreateFromYawPitchRoll(0.6f, 0.9f, 0.0f));
		SetWorld(_camera, _world.GetComponent<LocalTransform>(_camera).GetTransform());
	}

	private void SetWorld(Entity entity, Matrix4x4 localToWorld)
	{
		Matrix4x4.Invert(localToWorld, out var worldToLocal);
		ref var worldTransform = ref _world.GetComponent<WorldTransform>(entity);
		worldTransform.LocalToWorld = localToWorld;
		worldTransform.WorldToLocal = worldToLocal;
		_world.MarkWorldTransformChanged(entity);
	}

	// Left-handed look-at, inverted to a camera-to-world transform, matching the editor camera's convention.
	private static Matrix4x4 CreateCameraToWorld(Vector3 position, Vector3 target, Vector3 up)
	{
		var zAxis = Vector3.Normalize(target - position);
		var xAxis = Vector3.Normalize(Vector3.Cross(up, zAxis));
		var yAxis = Vector3.Cross(zAxis, xAxis);
		return new Matrix4x4(
			xAxis.X, xAxis.Y, xAxis.Z, 0.0f,
			yAxis.X, yAxis.Y, yAxis.Z, 0.0f,
			zAxis.X, zAxis.Y, zAxis.Z, 0.0f,
			position.X, position.Y, position.Z, 1.0f);
	}
}
