using System.Numerics;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor;

/// <summary>
/// The clip editor's private preview world. Poses are written straight into the model's
/// <see cref="Animator"/> by the editor rather than produced by an animation graph, so what is shown
/// is always the clip exactly as authored, including edits that have not been saved or imported.
/// </summary>
public sealed class AnimationClipPreviewScene : IEditorRenderViewSource, IBoneSpace, IDisposable
{
	private const float MinPitch = -1.45f;
	private const float MaxPitch = 1.45f;
	private const float MinDistance = 0.05f;
	private const float CameraFov = 45.0f;

	private readonly IRenderViewHost _host;
	private readonly EditorRenderViews _views;
	private readonly EditorViewportStateBus _viewUi;
	private readonly TransformSystem _transforms = new();
	// Matches the material preview. Shadows and the sky are prepared once per frame from the primary
	// view's sun, so with them on the preview would be lit at the authoring scene's sun angle rather
	// than by its own fixed light.
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
	private readonly Entity _animator;
	private Vector3 _cameraTarget;
	private float _cameraYaw = 0.45f;
	private float _cameraPitch = 0.25f;
	private float _cameraDistance = 3.0f;
	private bool _disposed;

	public AnimationClipPreviewScene(
		IRenderViewHost host,
		EditorRenderViews views,
		EditorViewportStateBus viewUi,
		IProjectAssetPipelineService pipeline,
		IEditorProjectService project,
		Guid modelId)
	{
		_host = host;
		_views = views;
		_viewUi = viewUi;

		if (project.TryGetAsset(modelId, out var asset) && asset.Type == AssetType.Prefab)
		{
			pipeline.InstantiatePrefab(project.CurrentAssetCatalog, modelId, new EditorScene { World = World });
		}
		else
		{
			pipeline.InstantiateImportedModel(project.CurrentAssetCatalog, modelId, World);
		}

		foreach (var entry in World.View<Animator>())
		{
			_animator = entry.Entity;
			break;
		}

		if (_animator.IsValid == false)
		{
			throw new InvalidOperationException("The preview model has no skeleton to animate.");
		}

		ref var animator = ref World.GetComponent<Animator>(_animator);
		Skeleton = animator.SkeletonAsset.Asset
			?? throw new InvalidOperationException("The preview model's skeleton is not available.");
		Pose = new Pose(Skeleton.BoneCount);
		Pose.SetToBindPose(Skeleton);

		// Detach the animator from any graph so nothing but this scene ever writes its pose.
		animator.GraphAsset = default;
		animator.ClipSetAsset = default;
		animator.Graph = null;
		animator.ClipSet = null;
		animator.GraphInstance = null;
		animator.Bindings = null;
		animator.BindPosePrepared = false;
		animator.Skeleton = Skeleton;
		animator.Pose = Pose;
		animator.SkinningMatrices = new Matrix4x4[Skeleton.BoneCount];
		animator.PreviousSkinningMatrices = new Matrix4x4[Skeleton.BoneCount];
		animator.HasPreviousPose = false;

		var light = World.CreateEntity("Clip Preview Light", Matrix4x4.CreateFromYawPitchRoll(0.4f, 0.65f, 0.0f));
		World.AddComponent(light, new Light
		{
			Type = LightType.Directional,
			Color = ColorRGBA.White,
			Intensity = 2.25f,
			Range = 25.0f,
			HorizonFade = true
		});

		var camera = new Camera
		{
			ScreenResolution = new Int2(512, 512)
		};
		camera.SetPerspective(CameraFov);
		_camera = World.CreateEntity("Clip Preview Camera", Matrix4x4.Identity);
		World.AddComponent(_camera, camera);

		ApplyPose();
		FrameAll();

		View = host.CreateView(new RenderViewDescriptor(World, "Animation Clip Preview", RenderViewOutput.Texture));
		views.Register(this);
	}

	public RenderViewId View { get; }
	public World World { get; } = new(WorldTag.Editor);

	/// <summary>The skeleton of the preview model. Pose bone indices refer to this skeleton.</summary>
	public Skeleton Skeleton { get; }

	/// <summary>Write bones here, then call <see cref="ApplyPose"/>.</summary>
	public Pose Pose { get; }

	/// <summary>Turns <see cref="Pose"/> into skinning matrices, sockets and bone world transforms.</summary>
	public void ApplyPose()
	{
		ref var animator = ref World.GetComponent<Animator>(_animator);
		if (animator.HasPreviousPose)
		{
			animator.SkinningMatrices!.AsSpan().CopyTo(animator.PreviousSkinningMatrices);
		}

		Pose.ComputeSkinningMatrices(Skeleton, animator.SkinningMatrices!);
		if (animator.HasPreviousPose == false)
		{
			animator.SkinningMatrices!.AsSpan().CopyTo(animator.PreviousSkinningMatrices);
			animator.HasPreviousPose = true;
		}

		animator.PoseGeneration++;
		AnimationSystem.ApplyExposedBones(World);
		_transforms.PreRender(0.0f, World);
	}

	/// <summary>World transform of a bone in the last applied pose.</summary>
	public Matrix4x4 GetBoneWorldMatrix(int boneIndex)
	{
		var animatorWorld = World.GetComponent<WorldTransform>(_animator).LocalToWorld;
		return Pose.GetModelSpaceMatrix(boneIndex) * animatorWorld;
	}

	/// <summary>World transform of a bone's parent, or of the rig root for a root bone.</summary>
	public Matrix4x4 GetBoneParentWorldMatrix(int boneIndex)
	{
		var parent = Skeleton.ParentIndices[boneIndex];
		return parent >= 0
			? GetBoneWorldMatrix(parent)
			: World.GetComponent<WorldTransform>(_animator).LocalToWorld;
	}

	/// <summary>Turns the camera around its target by the given yaw and pitch, in radians.</summary>
	public void Orbit(float yawDelta, float pitchDelta)
	{
		_cameraYaw += yawDelta;
		_cameraPitch = Math.Clamp(_cameraPitch + pitchDelta, MinPitch, MaxPitch);
		UpdateCamera();
	}

	/// <summary>Moves the camera and its target in the view plane by a fraction of the orbit distance.</summary>
	public void Pan(float right, float up)
	{
		var rotation = GetCameraRotation();
		var rightAxis = Vector3.Transform(Vector3.UnitX, rotation);
		var upAxis = Vector3.Transform(Vector3.UnitY, rotation);
		_cameraTarget += (rightAxis * right + upAxis * up) * _cameraDistance;
		UpdateCamera();
	}

	/// <summary>Scales the orbit distance; values below 1 move closer.</summary>
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

	/// <summary>Frames every bone of the current pose.</summary>
	public void FrameAll()
	{
		if (Skeleton.BoneCount == 0)
		{
			Frame(Vector3.Zero, 1.0f);
			return;
		}

		var min = new Vector3(float.MaxValue);
		var max = new Vector3(float.MinValue);
		for (var i = 0; i < Skeleton.BoneCount; i++)
		{
			var position = GetBoneWorldMatrix(i).Translation;
			min = Vector3.Min(min, position);
			max = Vector3.Max(max, position);
		}

		Frame((min + max) * 0.5f, Vector3.Distance(min, max) * 0.5f);
	}

	public bool PrepareSubmission(float deltaTime, out RenderViewSubmission submission)
	{
		var ui = _viewUi.GetUiState(View);
		if (_disposed || ui.Visible == false)
		{
			submission = default;
			return false;
		}

		ref var camera = ref World.GetComponent<Camera>(_camera);
		if (ui.ContentSizePixels.X > 0 &&
		    ui.ContentSizePixels.Y > 0 &&
		    camera.ScreenResolution != ui.ContentSizePixels)
		{
			// Keep the projection's aspect ratio matched to the panel so the image is not stretched.
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
		_host.DestroyView(View);
		if (_host is RenderGraph graph)
		{
			graph.ReleaseWorldSkinningResources(World);
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
