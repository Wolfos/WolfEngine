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
    private readonly RenderConfig _config = new();
    private bool _disposed;
    public RenderViewId View { get; }
    public World World { get; } = new(WorldTag.Editor);
    public AnimationGraphInstance Instance => World.GetComponent<Animator>(_animator).GraphInstance!;
    public AnimationPreviewScene(IRenderViewHost host, EditorRenderViews views, EditorViewportStateBus ui,
        IProjectAssetPipelineService pipeline, IEditorProjectService project, Guid modelId, AnimationGraph graph, AnimationSet? clips, Func<Guid, Type, object?>? resolve = null)
    {
        _host = host; _views = views; _ui = ui;
        if (project.TryGetAsset(modelId, out var asset) && asset.Type == AssetType.Prefab)
            pipeline.InstantiatePrefab(project.CurrentAssetCatalog, modelId, new EditorScene { World = World });
        else pipeline.InstantiateImportedModel(project.CurrentAssetCatalog, modelId, World);
        foreach (var entry in World.View<Animator>()) { _animator = entry.Entity; break; }
        if (!_animator.IsValid) throw new InvalidOperationException("Preview model must contain a skeleton and Animator.");
        ref var animator = ref World.GetComponent<Animator>(_animator);
        animator.GraphAsset = default; animator.ClipSetAsset = default; animator.Graph = graph; animator.ClipSet = clips; animator.GraphInstance = null;
        animator.Skeleton = animator.SkeletonAsset.Asset ?? throw new InvalidOperationException("Preview skeleton is unavailable.");
        animator.GraphInstance = AnimationGraphCompiler.Compile(graph, animator.Skeleton, clips, resolve ?? Animator.Resolve).CreateInstance();
        animator.Pose = animator.GraphInstance.Output;
        animator.SkinningMatrices = new Matrix4x4[animator.Skeleton.BoneCount];
        animator.PreviousSkinningMatrices = new Matrix4x4[animator.Skeleton.BoneCount];
        animator.HasPreviousPose = false; animator.Bindings = null;
        Instance.Playing = false;
        _animation.Update(0, World); _transforms.PreRender(0, World);
        var radius = 1f;
        foreach (var entry in World.View<SkinnedMeshRenderer>())
            if (entry.First.MeshAsset.Asset is { } mesh) radius = Math.Max(radius, mesh.BoundingSphere.Radius);
        radius = Math.Clamp(radius, 0.5f, 200);
        var light = World.CreateEntity("Animation Preview Light", Matrix4x4.CreateFromYawPitchRoll(0.4f, 0.7f, 0));
        World.AddComponent(light, new Light { Type = LightType.Directional, Color = ColorRGBA.White, Intensity = 2, Range = 25 });
        var camera = new Camera { ScreenResolution = new global::WolfEngine.Mathematics.Int2(512, 512) }; camera.SetPerspective(45);
        var position = new Vector3(radius * 1.4f, radius, -radius * 3);
        Matrix4x4.Invert(Matrix4x4.CreateLookAtLeftHanded(position, new Vector3(0, radius * 0.5f, 0), Vector3.UnitY), out var cameraWorld);
        _camera = World.CreateEntity("Animation Preview Camera", cameraWorld); World.AddComponent(_camera, camera);
        _transforms.PreRender(0, World);
        View = host.CreateView(new RenderViewDescriptor(World, "Animation Preview", RenderViewOutput.Texture)); views.Register(this);
    }
    public void Step(float delta) { var playing = Instance.Playing; Instance.Playing = true; _animation.Update(delta, World); Instance.Playing = playing; _transforms.PreRender(0, World); }
    public void Seek(float seconds) { Instance.Seek(seconds); _animation.Update(0, World); _transforms.PreRender(0, World); }
    public bool PrepareSubmission(float deltaTime, out RenderViewSubmission submission)
    {
        if (_disposed || !_ui.GetUiState(View).Visible) { submission = default; return false; }
        _animation.Update(deltaTime, World); _transforms.PreRender(deltaTime, World);
        submission = new(View, World.GetComponent<Camera>(_camera), World.GetComponent<WorldTransform>(_camera), _config);
        return true;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _views.Unregister(this); _ui.RemoveView(View); _host.DestroyView(View);
        if (_host is RenderGraph graph) graph.ReleaseWorldSkinningResources(World);
    }
}
