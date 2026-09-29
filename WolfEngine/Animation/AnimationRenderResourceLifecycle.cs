using WolfEngine.ECS;
using WolfEngine.Rendering;

namespace WolfEngine.Animation;

/// <summary>Retire private deformation ranges when a scene leaves the world manager.</summary>
public sealed class AnimationRenderResourceLifecycle(RenderGraph renderGraph) : IWorldRemovedListener
{
    public void OnWorldRemoved(World world) => renderGraph.ReleaseWorldSkinningResources(world);
}
