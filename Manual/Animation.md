# Animation

WolfEngine plays skeletal animation imported from FBX and glTF. A rigged source file produces
three kinds of asset: the meshes, one `Skeleton`, and one `AnimationClip` per animation take.

This first version plays a single clip per character. Blending, state machines, retargeting and an
in-engine curve editor are not implemented, but the asset format and the runtime seams are built
for them — see [Extending](#extending).

## Importing a rigged model

Drop a rigged `.fbx` or `.gltf`/`.glb` into `Assets/` and the pipeline imports it like any other 3D
source. Alongside the usual meshes, materials and textures you get:

- a **Skeleton** sub-asset holding bone names, the hierarchy, the bind pose and inverse bind matrices
- an **AnimationClip** sub-asset per take

Bones do **not** become entities. A character with 65 bones would otherwise push 65 transforms
through the ECS hierarchy every frame, which does not scale to crowds. The bone hierarchy lives in
the `Skeleton` asset, and the pose lives in a flat array on the `Animator`.

Dragging the model into a scene attaches a `SkinnedMeshRenderer` to each skinned mesh and one
shared `Animator` to the model root.

> **Source units.** Mixamo and many FBX exporters author in centimetres, so a character imports
> roughly 100× too large. Set the root entity's scale to 0.01 until an import-time scale setting
> exists.

## Components

`Animator` drives one skeleton:

| Field | Meaning |
| --- | --- |
| `SkeletonAsset` | The skeleton to pose. |
| `ClipAsset` | The clip to play. |
| `Speed` | Playback rate multiplier. |
| `Loop` | Whether the clip wraps or clamps at its end. |
| `Playing` | Set false to freeze; `Time` can still be edited to scrub. |
| `Time` | Playback position in seconds. |

`SkinnedMeshRenderer` draws a mesh deformed by an animator:

| Field | Meaning |
| --- | --- |
| `MeshAsset`, `MaterialAsset` | As `MeshRenderer`. |
| `SkeletonAsset` | Must match the skeleton the mesh was skinned to. |
| `AnimatorEntity` | Entity carrying the driving `Animator`. Defaults to the same entity. |
| `BoundsExpansion` | Culling bounds multiplier over the bind pose. |

Several skinned meshes sharing one animator is the normal arrangement — a body and its clothing are
separate renderers but one skeleton, and a per-mesh animator would let the parts drift apart.

### Attaching things to bones

Bones are not entities, so add an `ExposedBone` to opt one in as an attachment socket:

```csharp
var hand = world.CreateEntity("WeaponSocket");
world.SetParent(hand, characterEntity);
world.AddTransform(hand, Matrix4x4.Identity);
world.AddComponent(hand, new ExposedBone(characterEntity, "mixamorig:RightHand"));
```

`AnimationSystem` writes that bone's model-space transform into the entity's local transform each
frame, before `TransformSystem` propagates it. Anything parented to the socket follows the bone.

## How a frame runs

1. `AnimationSystem` (an `IUpdate`, so it lands before `TransformSystem`) advances each animator,
   samples the clip into a `Pose`, and turns the pose into skinning matrices.
2. `RenderPipeline` copies those matrices into the frame snapshot and registers each skinned
   instance for drawing.
3. On the render thread, `SkinningPass` runs a compute shader that writes the deformed vertices into
   each instance's private range of the packed vertex buffer.
4. Bottom-level acceleration structures for those instances are rebuilt from the new vertices.

Step 3 is why skinned characters are real geometry rather than a vertex-shader effect: they appear
correctly in ray-traced reflections and in DDGI, and they reuse the existing culling and
indirect-draw path with no shader variant.

Each instance owns a copy of the mesh's GPU vertex range, so instancing a character costs vertex
memory. The index buffer is shared with the source mesh.

## Non-skeletal animation

A clip carries two kinds of track, and both travel through the same sampler and the same blending:

- **transform tracks** — a local TRS, bound either to a skeleton bone or, by node path, to an
  arbitrary entity. An animated door or turret arrives this way with no separate system.
- **property tracks** — a single scalar bound to a named property, for things like a light's
  intensity.

The importer emits bone-bound transform tracks for channels that name a skeleton bone and
node-bound ones for everything else. There is no authoring UI for property tracks yet.

## Extending

The seams the unimplemented features attach to:

- **Animator graph.** Implement `IPoseSource`. `SingleClipPoseSource` is the current one;
  `Pose.Blend` already defines the blend contract a graph node would call.
- **Retargeting.** `BoneRemap` resolves a clip's tracks against a skeleton by bone name. Humanoid
  retargeting replaces that one lookup with a rig mapping plus per-bone basis correction. It works
  because clips address bones by name rather than index, store local-space TRS rather than baked
  matrices, and retain the bind pose they were authored against.
- **Curve editor.** Curves already carry an interpolation mode and optional tangents, and cubic
  Hermite is implemented, so authored curves need no format change.
- **BLAS refit.** Skinned acceleration structures are fully rebuilt each frame. Refitting would be
  substantially cheaper and is the obvious next step at higher character counts.
