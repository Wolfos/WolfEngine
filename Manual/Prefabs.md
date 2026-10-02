# Spawning prefabs at runtime

Gameplay code spawns a prefab with `World.Instantiate`. It works the same in the standalone runtime and in editor
Play mode. Hold the prefab as an `AssetRef<Prefab>` field on a component or data asset; the inspector shows an
asset picker for it.

```csharp
using System.Numerics;
using WolfEngine;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;

public struct Barracks : IEntityComponent
{
    public AssetRef<Prefab> Unit;
    public float Cooldown;
}

public sealed class BarracksSystem : IUpdate
{
    public void Update(float deltaTime, World world)
    {
        foreach (var entry in world.View<Barracks, WorldTransform>())
        {
            ref var barracks = ref entry.First;
            barracks.Cooldown -= deltaTime;
            if (barracks.Cooldown > 0.0f)
                continue;

            barracks.Cooldown = 5.0f;
            var position = entry.Second.LocalToWorld.Translation + new Vector3(0.0f, 0.0f, 4.0f);
            var unit = world.Instantiate(barracks.Unit, position, Quaternion.Identity);
            // `unit` is the instance's root entity.
        }
    }

    public WorldTag GetTag() => WorldTag.Game;
}
```

- `Instantiate(prefab)` spawns the root at the pose it was saved with.
- `Instantiate(prefab, position, rotation, parent)` replaces the root's position and rotation and keeps its scale.
  With a `parent`, the pose is relative to that parent.

Spawning inside a `View` loop is safe: entities and components added during the loop are not visited by it.

## Cost

The first spawn loads the prefab and builds a template from it. The template is cached until the asset changes,
or until a gameplay reload in the editor. Every later spawn only creates entities and copies component values.
To skip the per-call asset lookup, resolve the template once with `prefab.Asset` and call
`Prefab.Instantiate(world, ...)` on it.

How a component is copied depends on its type:

| Component holds | Per-spawn cost |
| --- | --- |
| Value types and strings | Copied. No allocation. |
| Value types and `Entity` fields that point at other entities in the prefab | Copied, then those fields are pointed at the new instance. No allocation. |
| Lists, arrays, other classes, or `Entity` fields alongside strings | Deserialized again, so instances never share mutable state. Allocates. |

Keep components on frequently spawned prefabs in the first two rows. An `Entity` field that points outside the
prefab spawns as an invalid entity, as it does in scenes.

## Builds

The build cooks every asset reachable from the build's scenes and from the explicit `AssetIds` in
`WolfEngineBuild.json`. A prefab referenced through an `AssetRef<Prefab>` in a scene component or a data asset is
reachable that way. A prefab that only gameplay code knows about has to be added to `AssetIds`. Otherwise
`Instantiate` throws in the runtime, because the prefab is not in any pack.

The runtime spawns the prefab file as it was last saved. That includes the stored values of prefabs nested inside
it. Play mode merges nested prefabs with their current source instead. If a nested prefab changes, re-save the
outer prefab so the built game matches Play mode. Every component in a cooked prefab must resolve to a type in
the gameplay assembly, or loading it fails. Play mode logs and skips components it cannot resolve.
