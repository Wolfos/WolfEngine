using System;
using System.Collections.Generic;
using System.Threading;
using WolfEngine.ECS;
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
    private readonly ManualResetEventSlim _frameReady = new(true);
    
    public RenderPipeline(
        RenderGraph renderGraph)
    {
        _renderGraph = renderGraph ?? throw new ArgumentNullException(nameof(renderGraph));
    }

    public void Run()
    {
        _renderGraph.FrameCompleted += _frameReady.Set;
        _renderGraph.Startup(static () => { }, static _ => { });
    }

    public void PublishSnapshot(Camera camera, WorldTransform cameraWorldTransform, IReadOnlyList<World> worlds)
    {
        if (worlds is null || worlds.Count == 0)
        {
            return;
        }

        _frameReady.Wait();
        _frameReady.Reset();

        var snapshot = _renderGraph.BeginSnapshotWrite();
        snapshot.SetCamera(camera, cameraWorldTransform);

        for (var i = 0; i < worlds.Count; i++)
        {
            var world = worlds[i];
            if (world is null)
            {
                continue;
            }

            foreach (var entry in world.View<WorldTransform, MeshRenderer>())
            {
                ref var transform = ref entry.First;
                ref var meshRenderer = ref entry.Second;
                var transformMatrix = transform.LocalToWorld;
                snapshot.AddDraw(meshRenderer.Mesh, meshRenderer.Material, transformMatrix);
            }

            foreach (var entry in world.View<WorldTransform, Light>())
            {
                ref var transform = ref entry.First;
                ref var light = ref entry.Second;
                snapshot.AddLight(light, transform.LocalToWorld);
            }
        }

        _renderGraph.PublishSnapshot();
    }
}
