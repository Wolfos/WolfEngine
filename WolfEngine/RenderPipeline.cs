using WolfEngine.ECS;
using WolfEngine.Profiling;
using WolfEngine.Rendering;

namespace WolfEngine;

public interface IRenderPipeline
{
    void Run();
    void PublishSnapshot(Camera camera, WorldTransform cameraWorldTransform, IReadOnlyList<World> worlds);
}

public class RenderPipeline : IRenderPipeline
{
    private readonly RenderGraph _renderGraph;
    private readonly GpuDrawDatabase _drawDatabase;
    private readonly ManualResetEventSlim _frameReady = new(true);
    
    public RenderPipeline(
        RenderGraph renderGraph,
        GpuDrawDatabase drawDatabase)
    {
        _renderGraph = renderGraph ?? throw new ArgumentNullException(nameof(renderGraph));
        _drawDatabase = drawDatabase ?? throw new ArgumentNullException(nameof(drawDatabase));
    }

    public void Run()
    {
        _renderGraph.FrameCompleted += _frameReady.Set;
        _renderGraph.Startup(static () => { }, static _ => { });
    }

    public void PublishSnapshot(Camera camera, WorldTransform cameraWorldTransform, IReadOnlyList<World> worlds)
    {
        using (FrameProfiler.Instance.Measure("Render Thread Wait"))
        {
            _frameReady.Wait();
        }
        _frameReady.Reset();

        using (FrameProfiler.Instance.Measure("Build Snapshot"))
        {
            var snapshot = _renderGraph.BeginSnapshotWrite();
            snapshot.SetCamera(camera, cameraWorldTransform);
            _drawDatabase.BeginSync();

            for (var i = 0; i < (worlds?.Count ?? 0); i++)
            {
                var world = worlds![i];
                if (world is null)
                {
                    continue;
                }

                foreach (var entry in world.View<WorldTransform, MeshRenderer>())
                {
                    ref var transform = ref entry.First;
                    ref var meshRenderer = ref entry.Second;
                    var transformMatrix = transform.LocalToWorld;
                    _drawDatabase.Touch(entry.Entity, meshRenderer.Mesh, meshRenderer.Material, transformMatrix);
                }

                foreach (var entry in world.View<WorldTransform, Light>())
                {
                    ref var transform = ref entry.First;
                    ref var light = ref entry.Second;
                    snapshot.AddLight(light, transform.LocalToWorld);
                }
            }

            _drawDatabase.EndSync();
            _renderGraph.PublishSnapshot();
        }
    }
}
