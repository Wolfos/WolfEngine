using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.UI;
using System.Numerics;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering;

namespace WolfEngine.Editor;

/// <summary>Private preview world, advanced exclusively by its transport, never by scene Play mode.</summary>
public sealed class AnimationPreviewScene : IEditorRenderViewSource, IDisposable
{
	private readonly IRenderViewHost _host;
	private readonly EditorRenderViews _views;
	private readonly EditorViewportStateBus _ui;
	private readonly AnimationSystem _animation = new();
	private readonly TransformSystem _transforms = new();
	private readonly Entity _camera;
	private readonly Entity _animator;
	// Matches the material and clip previews: shadows and the sky are prepared from the primary view's
	// sun, so with shadows on the preview would be lit at the authoring scene's sun angle.
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
	private bool _disposed;
	public RenderViewId View { get; }
	public World World { get; } = new(WorldTag.Editor);
	public AnimationGraphInstance Instance => World.GetComponent<Animator>(_animator).GraphInstance!;

	public AnimationPreviewScene(
		IRenderViewHost host,
		EditorRenderViews views,
		EditorViewportStateBus ui,
		IProjectAssetPipelineService pipeline,
		IEditorProjectService project,
		Guid modelId,
		AnimationGraph graph,
		AnimationSet? clips,
		Func<Guid, Type, object?>? resolve = null)
	{
		_host = host;
		_views = views;
		_ui = ui;
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

		if ((_animator.IsValid) == false)
		{
			throw new InvalidOperationException("Preview model must contain a skeleton and Animator.");
		}

		ref var animator = ref World.GetComponent<Animator>(_animator);
		animator.GraphAsset = default;
		animator.ClipSetAsset = default;
		animator.Graph = graph;
		animator.ClipSet = clips;
		animator.GraphInstance = null;
		animator.Skeleton = animator.SkeletonAsset.Asset ?? throw new InvalidOperationException("Preview skeleton is unavailable.");
		animator.GraphInstance = AnimationGraphCompiler.Compile(graph, animator.Skeleton, clips, resolve ?? Animator.Resolve).CreateInstance();
		animator.Pose = animator.GraphInstance.Output;
		animator.SkinningMatrices = new Matrix4x4[animator.Skeleton.BoneCount];
		animator.PreviousSkinningMatrices = new Matrix4x4[animator.Skeleton.BoneCount];
		animator.HasPreviousPose = false;
		animator.Bindings = null;
		Instance.Playing = false;
		_animation.Update(0, World);
		_transforms.PreRender(0, World);
		var radius = 1f;
		foreach (var entry in World.View<SkinnedMeshRenderer>())
		{
			if (entry.First.MeshAsset.Asset is { } mesh)
			{
				radius = Math.Max(radius, mesh.BoundingSphere.Radius);
			}
		}

		radius = Math.Clamp(radius, 0.5f, 200);
		var light = World.CreateEntity("Animation Preview Light", Matrix4x4.CreateFromYawPitchRoll(0.4f, 0.65f, 0));
		World.AddComponent(
			light,
			new Light { Type = LightType.Directional, Color = ColorRGBA.White, Intensity = 2.25f, Range = 25, HorizonFade = true });
		var camera = new Camera
		{
			ScreenResolution = new global::WolfEngine.Mathematics.Int2(512, 512)
		};
		camera.SetPerspective(45);
		var minimum = new Vector3(float.MaxValue);
		var maximum = new Vector3(float.MinValue);
		var animatorWorld = World.GetComponent<WorldTransform>(_animator).LocalToWorld;
		for (var bone = 0; bone < animator.Skeleton.BoneCount; bone++)
		{
			var boneWorld = animator.Pose.GetModelSpaceMatrix(bone) * animatorWorld;
			minimum = Vector3.Min(minimum, boneWorld.Translation);
			maximum = Vector3.Max(maximum, boneWorld.Translation);
		}

		// Joint bounds alone omit geometry above the head and outside the limbs.
		foreach (var entry in World.View<SkinnedMeshRenderer>())
		{
			if (entry.First.MeshAsset.Asset is not { } mesh) continue;

			var transform = World.GetComponent<WorldTransform>(entry.Entity).LocalToWorld;
			var meshCenter = Vector3.Transform(mesh.BoundingSphere.Center, transform);
			var scale = MathF.Max(
				new Vector3(transform.M11, transform.M12, transform.M13).Length(),
				MathF.Max(
					new Vector3(transform.M21, transform.M22, transform.M23).Length(),
					new Vector3(transform.M31, transform.M32, transform.M33).Length()));
			var extent = new Vector3(mesh.BoundingSphere.Radius * scale);
			minimum = Vector3.Min(minimum, meshCenter - extent);
			maximum = Vector3.Max(maximum, meshCenter + extent);
		}

		var center = animator.Skeleton.BoneCount > 0 ? (minimum + maximum) * 0.5f : Vector3.Zero;
		radius = MathF.Max(radius, animator.Skeleton.BoneCount > 0 ? Vector3.Distance(minimum, maximum) * 0.5f : 1.0f);
		var distance = radius / MathF.Sin(22.5f * MathF.PI / 180.0f) * 1.25f;
		var position = center + Vector3.Normalize(new Vector3(1.4f, 1.0f, -3.0f)) * distance;
		Matrix4x4.Invert(Matrix4x4.CreateLookAtLeftHanded(position, center, Vector3.UnitY), out var cameraWorld);
		_camera = World.CreateEntity("Animation Preview Camera", cameraWorld);
		World.AddComponent(_camera, camera);
		_transforms.PreRender(0, World);
		View = host.CreateView(new RenderViewDescriptor(World, "Animation Preview", RenderViewOutput.Texture));
		views.Register(this);
	}

	public void Step(float delta)
	{
		var playing = Instance.Playing;
		Instance.Playing = true;
		_animation.Update(delta, World);
		Instance.Playing = playing;
		_transforms.PreRender(0, World);
	}

	public void Seek(float seconds)
	{
		Instance.Seek(seconds);
		_animation.Update(0, World);
		_transforms.PreRender(0, World);
	}

	public bool PrepareSubmission(float deltaTime, out RenderViewSubmission submission)
	{
		if (_disposed || (_ui.GetUiState(View).Visible) == false)
		{
			submission = default;
			return false;
		}

		_animation.Update(deltaTime, World);
		_transforms.PreRender(deltaTime, World);
		submission = new(
			View,
			World.GetComponent<Camera>(_camera),
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
		_ui.RemoveView(View);
		_host.DestroyView(View);
		if (_host is RenderGraph graph)
		{
			graph.ReleaseWorldSkinningResources(World);
		}
	}
}
