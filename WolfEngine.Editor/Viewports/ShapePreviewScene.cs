using System.Numerics;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
using WolfEngine.Mathematics;
using WolfEngine.Modeling;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor;

/// <summary>
/// The Model workspace's private preview world: the compiled mesh, a light and an orbit camera. The mesh is swapped
/// whenever a compile lands, so the preview always shows the shape as it is now, saved or not.
/// <para>
/// Shape space is world space here: the mesh entity sits at the origin with no transform, so gizmo and picking maths
/// can use shape coordinates directly.
/// </para>
/// </summary>
public sealed class ShapePreviewScene : IEditorRenderViewSource, IDisposable
{
	private const float MinPitch = -1.45f;
	private const float MaxPitch = 1.45f;
	private const float MinDistance = 0.05f;
	private const float CameraFov = 45.0f;

	// A replaced mesh may still be referenced by draw records built for frames already in flight. Its GPU memory is
	// released only after this many more preview submissions, by which time nothing refers to it.
	private const int RetireAfterSubmissions = 4;

	private readonly RenderGraph _renderGraph;
	private readonly EditorRenderViews _views;
	private readonly EditorViewportStateBus _viewUi;
	private readonly TransformSystem _transforms = new();

	// Matches the material preview: the preview has its own fixed light, which shadows and the sky would override
	// with the authoring scene's sun.
	private readonly RenderConfig _config = new()
	{
		AntiAliasing = new AntiAliasingConfig
		{
			Enabled = false
		},
		ShadowMaps = new ShadowMapConfig
		{
			Enabled = false
		}
	};

	private readonly Entity _camera;
	private readonly Entity _model;
	private readonly List<(Mesh Mesh, int SubmissionsLeft)> _retiring = new();
	private ShapeMesh? _shownMesh;
	private Guid _shownMaterialId;
	private Vector3 _cameraTarget;
	private float _cameraYaw = 0.6f;
	private float _cameraPitch = 0.45f;
	private float _cameraDistance = 3.0f;
	private bool _disposed;

	public ShapePreviewScene(RenderGraph renderGraph, EditorRenderViews views, EditorViewportStateBus viewUi)
	{
		_renderGraph = renderGraph;
		_views = views;
		_viewUi = viewUi;

		var light = World.CreateEntity("Shape Preview Light", Matrix4x4.CreateFromYawPitchRoll(0.4f, 0.65f, 0.0f));
		World.AddComponent(light, new Light
		{
			Type = LightType.Directional,
			Color = ColorRGBA.White,
			Intensity = 2.25f,
			Range = 25.0f,
			HorizonFade = true
		});

		_model = World.CreateEntity("Shape", Matrix4x4.Identity);
		World.AddComponent(_model, new MeshRenderer());

		var camera = new Camera
		{
			ScreenResolution = new Int2(512, 512)
		};
		camera.SetPerspective(CameraFov);
		_camera = World.CreateEntity("Shape Preview Camera", Matrix4x4.Identity);
		World.AddComponent(_camera, camera);

		UpdateCamera();
		View = renderGraph.CreateView(new RenderViewDescriptor(World, "Shape Preview", RenderViewOutput.Texture));
		views.Register(this);
	}

	public RenderViewId View { get; }
	public World World { get; } = new(WorldTag.Editor);

	public Vector3 CameraPosition => GetCameraPosition();

	/// <summary>Shows a compiled mesh with a material, or nothing for null or an empty mesh.</summary>
	public void SetMesh(ShapeMesh? mesh, Guid materialId)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (ReferenceEquals(mesh, _shownMesh) && materialId == _shownMaterialId)
		{
			return;
		}

		ref var renderer = ref World.GetComponent<MeshRenderer>(_model);
		if (ReferenceEquals(mesh, _shownMesh) == false)
		{
			if (renderer.Mesh is { } previous)
			{
				_retiring.Add((previous, RetireAfterSubmissions));
			}

			renderer.Mesh = mesh is { IsEmpty: false } ? ShapeMeshCooker.ToRenderMesh(mesh) : null;
			_shownMesh = mesh;
		}

		if (materialId != _shownMaterialId || renderer.Material is null)
		{
			var material = new AssetRef<Material>
			{
				NodeId = materialId == Guid.Empty ? BuiltInEngineAssets.DefaultMaterial.NodeId : materialId
			};
			renderer.AssignMaterialAsset(material, _renderGraph);
			_shownMaterialId = materialId;
		}

		// Mesh gathering visits entities whose transform changed; this is how it notices the new geometry.
		World.MarkWorldTransformChanged(_model);
	}

	public void Orbit(float yawDelta, float pitchDelta)
	{
		_cameraYaw += yawDelta;
		_cameraPitch = Math.Clamp(_cameraPitch + pitchDelta, MinPitch, MaxPitch);
		UpdateCamera();
	}

	public void Pan(float right, float up)
	{
		var rotation = GetCameraRotation();
		var rightAxis = Vector3.Transform(Vector3.UnitX, rotation);
		var upAxis = Vector3.Transform(Vector3.UnitY, rotation);
		_cameraTarget += (rightAxis * right + upAxis * up) * _cameraDistance;
		UpdateCamera();
	}

	public void Zoom(float factor)
	{
		if (factor <= 0.0f || float.IsFinite(factor) == false)
		{
			return;
		}

		_cameraDistance = MathF.Max(MinDistance, _cameraDistance * factor);
		UpdateCamera();
	}

	public void Frame(Vector3 center, float radius)
	{
		var halfFov = float.DegreesToRadians(CameraFov) * 0.5f;
		_cameraTarget = center;
		_cameraDistance = MathF.Max(MinDistance, MathF.Max(radius, 0.05f) / MathF.Sin(halfFov) * 1.1f);
		UpdateCamera();
	}

	public void Frame(ShapeBounds bounds)
	{
		if (bounds.IsEmpty)
		{
			Frame(Vector3.Zero, 1.0f);
			return;
		}

		Frame(bounds.Center, bounds.Size.Length() * 0.5f);
	}

	/// <summary>A ray through a point on the image, in normalised device coordinates (-1 to 1, +Y up).</summary>
	public bool TryGetRay(Vector2 ndc, out Vector3 origin, out Vector3 direction)
	{
		var renderState = _viewUi.GetRenderState(View);
		if (renderState.ViewProjection.IsValid == false ||
		    Matrix4x4.Invert(renderState.ViewProjection.WorldToClip, out var clipToWorld) == false)
		{
			origin = default;
			direction = default;
			return false;
		}

		var near = Vector4.Transform(new Vector4(ndc, 0.0f, 1.0f), clipToWorld);
		var far = Vector4.Transform(new Vector4(ndc, 1.0f, 1.0f), clipToWorld);
		var nearPoint = new Vector3(near.X, near.Y, near.Z) / near.W;
		var farPoint = new Vector3(far.X, far.Y, far.Z) / far.W;
		origin = nearPoint;
		direction = Vector3.Normalize(farPoint - nearPoint);
		return float.IsFinite(direction.X);
	}

	public bool PrepareSubmission(float deltaTime, out RenderViewSubmission submission)
	{
		var ui = _viewUi.GetUiState(View);
		if (_disposed || ui.Visible == false)
		{
			submission = default;
			return false;
		}

		AdvanceRetirement();

		ref var camera = ref World.GetComponent<Camera>(_camera);
		if (ui.ContentSizePixels.X > 0 &&
		    ui.ContentSizePixels.Y > 0 &&
		    camera.ScreenResolution != ui.ContentSizePixels)
		{
			camera.ScreenResolution = ui.ContentSizePixels;
			camera.SetPerspective(CameraFov);
		}

		_transforms.PreRender(deltaTime, World);
		submission = new RenderViewSubmission(
			View,
			camera,
			World.GetComponent<WorldTransform>(_camera),
			_config);
		return true;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_views.Unregister(this);
		_viewUi.RemoveView(View);
		_renderGraph.DestroyView(View);

		// The view is gone, so nothing will draw these again; the render graph still waits for the GPU before freeing.
		if (World.GetComponent<MeshRenderer>(_model).Mesh is { } shown)
		{
			_renderGraph.ReleaseMeshResources(shown);
		}

		foreach (var (mesh, _) in _retiring)
		{
			_renderGraph.ReleaseMeshResources(mesh);
		}

		_retiring.Clear();
	}

	private void AdvanceRetirement()
	{
		for (var i = _retiring.Count - 1; i >= 0; i--)
		{
			var (mesh, left) = _retiring[i];
			if (left > 0)
			{
				_retiring[i] = (mesh, left - 1);
				continue;
			}

			_renderGraph.ReleaseMeshResources(mesh);
			_retiring.RemoveAt(i);
		}
	}

	private Quaternion GetCameraRotation()
	{
		Matrix4x4.Invert(Matrix4x4.CreateLookAtLeftHanded(GetCameraPosition(), _cameraTarget, Vector3.UnitY), out var cameraWorld);
		Matrix4x4.Decompose(cameraWorld, out _, out var rotation, out _);
		return rotation;
	}

	private Vector3 GetCameraPosition()
	{
		var direction = new Vector3(
			MathF.Cos(_cameraPitch) * MathF.Sin(_cameraYaw),
			MathF.Sin(_cameraPitch),
			-MathF.Cos(_cameraPitch) * MathF.Cos(_cameraYaw));
		return _cameraTarget + direction * _cameraDistance;
	}

	private void UpdateCamera()
	{
		World.SetLocalTransform(_camera, GetCameraPosition(), GetCameraRotation(), Vector3.One);
		_transforms.PreRender(0.0f, World);
	}
}
