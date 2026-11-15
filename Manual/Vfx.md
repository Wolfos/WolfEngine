# Visual effects

Visual effects are particles and trails: footstep dust, sword trails, hit sparks. An effect is authored as a
`VfxAsset`, previewed in the editor with the real renderer, and played from gameplay without effect-specific code.
Particles are simulated on the GPU; nothing is read back to the CPU.

## Effects and emitters

A `VfxAsset` holds one or more emitters. They start together when the effect plays and each runs on its own timing,
so one asset can combine a flash, sparks and a shockwave.

There are two kinds of emitter:

- **Sprite** emitters spawn particles drawn as textured quads. They face the camera, lie flat on the ground, or
  stretch along their velocity.
- **Ribbon** emitters draw a connected strip swept by two points on whatever the effect follows, such as a blade's
  root and tip. Use them for trails. Do not fake a trail with sprites.

Every emitter has a start delay and a duration. A duration of zero runs until the effect is stopped. A looping
emitter restarts after its duration and replays its bursts. An effect ends on its own once every emitter has
finished and its last particle has died, so one-shot effects need no cleanup.

Colour and opacity over a particle's life, size over its life, and a ribbon's width along its length are simple
curves: linear segments between keys. Intensity multiplies the colour, so values above one reach into bloom.

## Authoring

Create an effect with **Create > Data Asset > VfxAsset** in the Assets window and select it. The Asset Editor shows:

- **A preview** rendered by the game's renderer in its own world. It plays independently of Play mode, with
  Play, Pause, Step, Restart, Loop and a speed control. Drag to orbit and scroll to zoom.
- **Motion**, which moves the effect so movement effects can be judged: *Circle* walks it around the ground, for
  dust; *Swing* sweeps it like a sword, for trails.
- **Preview model**, a model or prefab to play the effect on. The model's animator plays its default state, and
  **Attach to** picks the entity the effect follows, such as the sword.
- **The emitter list** and the selected emitter's properties.

Edits show in the preview immediately. Saving happens on its own once you stop dragging a value, and effects already
playing in Play mode pick up the saved asset on their next frame.

Textures for effects are usually white masks whose colour comes from the effect. Set their importer's
**Semantic** to **Base Color Transparent**: the default **Base Color** compression keeps only one bit of alpha, which
turns soft puffs into hard cut-outs. An emitter without a texture draws a soft round puff, or a strip with soft
edges for ribbons.

## Playing effects from gameplay

### The `VisualEffect` component

Add `VisualEffect` to an entity to play an effect on it and follow it, with no code. The `Playing` field turns it on
and off: a continuous effect plays while it is on, and a one-shot effect plays once each time it turns on. Turning it
off lets the particles that are alive finish.

To follow a bone, put the component on that bone's socket entity (see `ExposedBone` in the animation manual) or on a
child of it, such as a weapon. To offset an effect, put it on a child entity.

### `IVfxService`

Get `IVfxService` from the service provider for effects that gameplay triggers:

```csharp
using System.Numerics;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Vfx;

public sealed class HitSparks(IVfxService vfx)
{
    public AssetRef<VfxAsset> Sparks;

    public void OnHit(World world, Vector3 point) =>
        vfx.Play(world, Sparks, point, Quaternion.Identity);
}
```

- `Play(world, effect, position, rotation)` plays at a fixed place, for impacts and other one-shots.
- `Play(world, effect, entity)` follows an entity until it is destroyed, then stops emitting where it last was.
- `Stop(handle)` stops emitting and lets alive particles finish; `Stop(handle, immediate: true)` removes them.
- `IsPlaying(handle)` and `SetTransform(handle, …)` work on the returned `VfxHandle`. Handles are safe to keep: once
  the effect has finished, every call ignores them.

Effects advance with their world, so they pause with Play mode. A private world, such as a tool's preview, calls
`IVfxService.Update(world, deltaTime)` itself after its transforms are up to date.

## Budgets

Each world has a pool of 32768 particles. A sprite emitter reserves **Max particles** slots of it for every instance
that is playing, and keeps them until its last particle has died. When an emitter is full, a new particle replaces its
oldest one. When the pool itself is full, a new emitter does not emit until other effects finish, and a warning is
logged once. Keep Max particles close to what an emitter actually keeps alive: roughly its rate times its longest
lifetime, plus its bursts.

The preview shows how many particles its effect reserves. `IVfxService.GetStatistics(world)` reports the same for
any world, and the GPU profiler reports the `VFX Simulate` and `VFX` passes.

## Rendering

Effects are unlit and draw after transparent geometry, before temporal anti-aliasing, into the lit HDR scene.

- **Additive** emitters add light and need no sorting. Use them for sparks, flashes and glows.
- **Alpha-blended** emitters are sorted back to front by their position, and particles within one emitter always draw
  oldest first, so they never flicker. Two alpha-blended emitters that overlap in depth can still sort the wrong way
  round, and effects always draw over transparent meshes, even one in front of them.
- **Soft distance** fades an effect out where it meets opaque geometry, hiding hard intersections with the ground.
- **Sprite sheets** play their frames over a particle's life, or at a fixed frame rate.

Because effects draw unlit, their colour does not follow the scene's lighting. Raise Intensity for effects that should
sit alongside sunlit surfaces, such as dust.

## Limits

- Particles do not collide, and there are no sub-emitters, force fields or mesh particles.
- Gravity in a local-space emitter points down the emitter's own Y axis.
- Ribbons sample their points once per frame, smoothed by Catmull-Rom subdivision. A very fast swing at a low frame
  rate is smooth but can still cut its corners.
- Effects do not write the FSR3 reactive mask, so FSR3 can smear fast effects slightly more than TAA does.
- Animation markers do not yet trigger effects directly; play effects from the gameplay code that handles markers.
