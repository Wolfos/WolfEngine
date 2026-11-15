using System.Numerics;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.UI;
using WolfEngine.Vfx;

namespace WolfEngine.Editor;

/// <summary>How the preview moves the effect, so movement effects such as dust and trails can be judged.</summary>
public enum VfxPreviewMotion
{
	Still,

	/// <summary>Walks a circle along the ground, for dust and other distance-driven effects.</summary>
	Circle,

	/// <summary>Sweeps an arc around the centre, like a sword swing, for trails.</summary>
	Swing
}

/// <summary>
/// The VFX editor's private world: a ground plane, a light, a camera and an anchor the effect plays on. It is
/// advanced only by its own transport, never by scene Play mode, and renders through the normal frame graph, so
/// what it shows is what the game shows.
/// </summary>
public sealed class VfxPreviewScene : IEditorRenderViewSource, IDisposable
{
	private const float CircleRadius = 2.0f;
	private const float CircleSpeed = 2.5f;
	private const float SwingRadius = 0.6f;
	private const float SwingPeriod = 0.9f;

	private readonly IRenderViewHost _host;
	private readonly EditorRenderViews _views;
	private readonly EditorViewportStateBus _ui;
	private readonly IVfxService _vfx;
	private readonly AnimationSystem _animation = new();
	private readonly TransformSystem _transforms = new();
	private readonly Entity _camera;
	private readonly Entity _anchor;
	// Matches the material and animation previews: the sky and shadows are prepared from the primary view's sun.
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
	private readonly List<Entity> _modelEntities = new();
	private VfxAsset? _effect;
	private VfxHandle _handle;
	private Entity _attachment;
	private float _motionTime;
	private int _framesSinceDrawn;
	private bool _disposed;

	public VfxPreviewScene(IRenderViewHost host, EditorRenderViews views, EditorViewportStateBus ui, IVfxService vfx)
	{
		_host = host ?? throw new ArgumentNullException(nameof(host));
		_views = views ?? throw new ArgumentNullException(nameof(views));
		_ui = ui ?? throw new ArgumentNullException(nameof(ui));
		_vfx = vfx ?? throw new ArgumentNullException(nameof(vfx));

		var light = World.CreateEntity("VFX Preview Light", Matrix4x4.CreateFromYawPitchRoll(0.4f, 0.75f, 0.0f));
		World.AddComponent(light, new Light
		{
			Type = LightType.Directional,
			Color = ColorRGBA.White,
			Intensity = 2.25f,
			Range = 25.0f,
			HorizonFade = true
		});

		var ground = World.CreateEntity(
			"VFX Preview Ground",
			Matrix4x4.CreateScale(12.0f, 0.1f, 12.0f) * Matrix4x4.CreateTranslation(0.0f, -0.05f, 0.0f));
		World.AddComponent(ground, new DebugPrimitiveRenderer
		{
			PrimitiveType = DebugPrimitiveType.Box,
			Tint = new ColorRGBA(0.32f, 0.36f, 0.3f, 1.0f),
			AlphaMode = AlphaMode.Opaque
		});

		_anchor = World.CreateEntity("VFX Preview Anchor", Matrix4x4.Identity);

		var camera = new Camera
		{
			ScreenResolution = new Mathematics.Int2(512, 384)
		};
		camera.SetPerspective(45.0f);
		_camera = World.CreateEntity("VFX Preview Camera", Matrix4x4.Identity);
		World.AddComponent(_camera, camera);
		SetOrbit(0.6f, 0.45f, 6.0f);

		View = host.CreateView(new RenderViewDescriptor(World, "VFX Preview", RenderViewOutput.Texture));
		_transforms.PreRender(0.0f, World);
		views.Register(this);
	}

	public RenderViewId View { get; }
	public World World { get; } = new(WorldTag.Editor);

	public bool Playing { get; set; } = true;
	public bool Loop { get; set; } = true;
	public float Speed { get; set; } = 1.0f;
	public VfxPreviewMotion Motion { get; set; } = VfxPreviewMotion.Still;

	/// <summary>Seconds since the effect last started.</summary>
	public float Time { get; private set; }

	public bool IsEffectPlaying => _vfx.IsPlaying(_handle);

	public VfxStatistics Statistics => _vfx.GetStatistics(World);

	/// <summary>Entities of the preview model an effect can be attached to, by name.</summary>
	public IReadOnlyList<Entity> ModelEntities => _modelEntities;

	public Entity Attachment => _attachment;

	public string GetName(Entity entity) =>
		World.IsAlive(entity) && World.HasComponent<NameComponent>(entity)
			? World.GetComponent<NameComponent>(entity).Name
			: "Entity";

	/// <summary>Plays <paramref name="effect"/> from the start, stopping whatever played before.</summary>
	public void Restart(VfxAsset? effect)
	{
		_vfx.Stop(_handle, immediate: true);
		_effect = effect;
		Time = 0.0f;
		_handle = effect is null
			? VfxHandle.Invalid
			: _vfx.Play(World, effect, VfxPlacement.AttachedTo(_attachment.IsValid ? _attachment : _anchor, Matrix4x4.Identity));
	}

	/// <summary>Places the camera on a sphere around the anchor.</summary>
	public void SetOrbit(float yaw, float pitch, float distance)
	{
		var target = new Vector3(0.0f, 0.75f, 0.0f);
		var direction = new Vector3(
			MathF.Sin(yaw) * MathF.Cos(pitch),
			MathF.Sin(pitch),
			-MathF.Cos(yaw) * MathF.Cos(pitch));
		var position = target + direction * MathF.Max(distance, 0.5f);
		Matrix4x4.Invert(Matrix4x4.CreateLookAtLeftHanded(position, target, Vector3.UnitY), out var cameraToWorld);
		World.SetWorldTransform(_camera, cameraToWorld.Translation, Quaternion.CreateFromRotationMatrix(cameraToWorld));
	}

	/// <summary>
	/// Replaces the preview model. Its animator plays its graph's default state, and the effect can follow any of
	/// its entities, such as a weapon or a bone socket.
	/// </summary>
	public void SetModel(IProjectAssetPipelineService pipeline, IEditorProjectService project, Guid modelId)
	{
		foreach (var entity in _modelEntities)
		{
			if (World.IsAlive(entity))
			{
				World.DestroyEntity(entity);
			}
		}

		_modelEntities.Clear();
		_attachment = default;
		if (modelId == Guid.Empty)
		{
			Restart(_effect);
			return;
		}

		var before = new List<Entity>();
		World.GetAllEntities(before);
		if (project.TryGetAsset(modelId, out var asset) && asset.Type == AssetType.Prefab)
		{
			pipeline.InstantiatePrefab(project.CurrentAssetCatalog, modelId, new EditorScene
			{
				World = World
			});
		}
		else
		{
			pipeline.InstantiateImportedModel(project.CurrentAssetCatalog, modelId, World);
		}

		var after = new List<Entity>();
		World.GetAllEntities(after);
		var existing = new HashSet<Entity>(before);
		foreach (var entity in after)
		{
			if (existing.Contains(entity) == false)
			{
				_modelEntities.Add(entity);
			}
		}

		// The model's root follows the anchor, so preview motion walks the whole character.
		foreach (var entity in _modelEntities)
		{
			if (World.HasComponent<Parent>(entity) == false)
			{
				World.SetParent(entity, _anchor);
			}
		}

		_animation.Update(0.0f, World);
		_transforms.PreRender(0.0f, World);
		Restart(_effect);
	}

	public void SetAttachment(Entity entity)
	{
		_attachment = entity;
		Restart(_effect);
	}

	/// <summary>
	/// Called by the editor every frame it shows the preview. Viewport visibility is only ever published by the panel
	/// that draws it, so once the panel stops drawing, this is what stops the preview rendering and simulating.
	/// </summary>
	public void MarkDrawn() => _framesSinceDrawn = 0;

	public bool PrepareSubmission(float deltaTime, out RenderViewSubmission submission)
	{
		_framesSinceDrawn++;
		if (_disposed || _framesSinceDrawn > 2 || _ui.GetUiState(View).Visible == false)
		{
			submission = default;
			return false;
		}

		var step = Playing ? MathF.Max(deltaTime, 0.0f) * MathF.Max(Speed, 0.0f) : 0.0f;
		Advance(step);
		submission = new RenderViewSubmission(
			View,
			World.GetComponent<Camera>(_camera),
			World.GetComponent<WorldTransform>(_camera),
			_config);

		return true;
	}

	/// <summary>Advances motion, animation and the effect by <paramref name="step"/> seconds of preview time.</summary>
	public void Advance(float step)
	{
		_motionTime += step;
		World.SetWorldTransform(_anchor, GetAnchorPosition(), GetAnchorRotation());
		_animation.Update(step, World);
		_transforms.PreRender(step, World);
		_vfx.Update(World, step);
		Time += step;

		if (Loop && _effect is not null && step > 0.0f && _vfx.IsPlaying(_handle) == false)
		{
			Restart(_effect);
		}
	}

	private Vector3 GetAnchorPosition()
	{
		return Motion switch
		{
			VfxPreviewMotion.Circle => new Vector3(
				MathF.Cos(_motionTime * CircleSpeed / CircleRadius) * CircleRadius,
				0.0f,
				MathF.Sin(_motionTime * CircleSpeed / CircleRadius) * CircleRadius),
			VfxPreviewMotion.Swing => new Vector3(0.0f, 0.75f, 0.0f),
			_ => Vector3.Zero
		};
	}

	private Quaternion GetAnchorRotation()
	{
		if (Motion == VfxPreviewMotion.Circle)
		{
			// Face along the circle, as a walking character would.
			var angle = _motionTime * CircleSpeed / CircleRadius;
			return Quaternion.CreateFromAxisAngle(Vector3.UnitY, -angle);
		}

		if (Motion == VfxPreviewMotion.Swing)
		{
			// A fast sweep, a pause, then the next sweep: the rhythm of a sword combo.
			var phase = _motionTime % SwingPeriod / SwingPeriod;
			var sweep = Math.Clamp(phase / 0.35f, 0.0f, 1.0f);
			var eased = 1.0f - (1.0f - sweep) * (1.0f - sweep);
			var yaw = (eased - 0.5f) * MathF.PI * 1.4f;
			// Lay the anchor's up axis, which ribbons use for root to tip by default, out along the sweep.
			return Quaternion.Concatenate(
				Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI * 0.5f),
				Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));
		}

		return Quaternion.Identity;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_vfx.StopAll(World, immediate: true);
		_views.Unregister(this);
		_ui.RemoveView(View);
		_host.DestroyView(View);
		if (_host is RenderGraph graph)
		{
			graph.ReleaseWorldSkinningResources(World);
		}
	}
}
