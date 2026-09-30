# Animation graphs

Every animator evaluates a graph, including ordinary single-clip playback. Gameplay C# owns movement, facing, action selection, damage timing and projectile creation. Graphs compose presentation poses; they cannot call gameplay code.

## Authoritative assets

| Source | Purpose |
| --- | --- |
| `.animgraph.json` | Typed parameters, pose nodes, constrained state machines, output and separate canvas layout. |
| `.data.json` (AnimationSet) | Rig reference and named clip-slot assignments. |
| `.data.json` (AnimationSequence) | Imported clip reference, playback-speed multiplier, scalar curves and timed presentation markers. |
| `.data.json` (BoneMask) | Rig reference and weighted bones, optionally including descendants. |

These files live under `Assets`. References use persistent metadata GUIDs. The asset pipeline records dependencies and produces Library artifacts. Edit curves and markers in the wrapper asset: FBX reimport replaces imported skeletal keys without replacing this metadata. Skeletal keys are read-only.

For animation-only FBXs, set **Animation skeleton** in the model importer before reimporting. Binding validates joint names, ancestry, invariant joint offsets and units against the target. The target rig supplies the bind pose; the animation-only export's first animated frame is not treated as a new bind pose. Genuine entity-transform channels remain entity channels. Explicit ignored-channel settings handle known export defects and should be used narrowly. Imported skinned meshes preserve their full bind transform into rig coordinates. Rendering converts the shared rig palette into each mesh's bind coordinates for current and previous poses; sockets continue using rig coordinates.

## Components and gameplay API

`Animator` has `SkeletonAsset`, `GraphAsset` and `ClipSetAsset`. An unassigned graph displays the bind pose. All renderers sharing a rig reference one animator. Bones stay in flat arrays; `ExposedBone` provides opt-in entity sockets for equipment.

Resolve typed handles when the graph instance changes, then reuse them:

```csharp
var instance = animator.Instance;
var speed = instance.Program.GetParameter("Speed", AnimationParameterType.Float);
var dead = instance.Program.GetParameter("Dead", AnimationParameterType.Bool);
// In gameplay updates:
instance.SetFloat(speed, actualMovementSpeed);
instance.SetBool(dead, isDead);
```

Use `AnimationActionHandles` and `SetAction` for externally timed actions. The input contains integer kind, variant and sequence ID plus normalized time. Increment the sequence for another occurrence of the same action. Animation follows gameplay's phase; presentation markers do not confer damage or projectile authority. Consume `Markers` after evaluation and before the next evaluation replaces the queue. Resolve a curve handle with `Program.GetCurve` and read it with `GetCurve`.

Graph nodes include bind pose, clip, two-pose blend, integer/bool selection, synchronized 1D locomotion, state machine, weighted bone-mask blend, scalar-curve remap and output. State inputs are pose subgraphs; transitions use typed parameter comparisons and clip progress. Authored node speed multiplies clip-wrapper playback speed. Looping, start time and restart policy belong to clip nodes. Non-looping clips hold the final pose.

Locomotion thresholds represent actual movement speeds. Walk and run share normalized phase and adjust playback frequency with measured speed. Use in-place assets and inspect displacement rather than relying on filenames. Clip sets substitute slots within a compatible rig family; missing slots or incompatible rigs produce diagnostics before playback.

Bone channels default to bind pose. Entity-transform and scalar-property channels default to captured target values. Channels blend by binding identity, independent of imported track order. Extend `AnimationPropertyBindings.Register<T>` with typed component getters/setters for additional property targets; unresolved targets report diagnostics.

## Animation workspace

Open a graph from Assets to use the **Animation** workspace. Edit clip sets, clip metadata and bone masks as DataAssets in the **Asset Editor**. It has Assets on the left, Animation in the center, Asset Editor on the right and Log below. Existing workspace layouts and the active workspace are preserved when this workspace is first seeded. Rename/delete/save it normally.

The Animation window has draggable dividers between graph and preview and between canvas and node inspector. The graph starts with most of the space. The Animation window provides a node canvas with middle-button pan, wheel zoom, node dragging, node creation/deletion and pose connections. Connect an output button to a pose-input button, or choose inputs in the selected-node inspector. State-machine inputs can be inspected as subgraphs. Undo/redo preserves node IDs and layout. Save writes authoritative JSON through the normal asset refresh path.

Select a preview model or curated prefab and a clip set. The isolated preview uses the runtime evaluator with play/pause, step, speed and scrub controls, parameter editing and evaluation inspection. Scrubbing emits no markers. Invalid edits retain the last valid compiled preview and show a diagnostic. Hidden previews stop advancing and submitting render views. Closing the project/window releases the preview view and retires its private deformed geometry. Scene animators can be inspected read-only.

Curve wrappers support constant (step), linear and cubic Hermite interpolation, tangent editing, key insertion/removal and timed named markers. Imported skeletal keys remain read-only.

## Frame order and scheduling

Gameplay writes inputs, animation samples once per shared subgraph, resolved outputs/sockets are applied, transforms propagate, then rendering snapshots the result. `AdvanceClock` and `EvaluatePose` separate logical time from pose sampling for later crowd scheduling. Marker intervals account for skipped samples and loop boundaries. Exited state branches stop delivering markers; interrupted transitions capture their current blended pose.

Pose generations increment only when skeletal matrices change. Skinning dispatches and skinned BLAS updates stop for unchanged poses; rendered-pose history still settles motion vectors after a change. Set `WOLF_FORCE_SKINNING_UPDATES=1` or use the automation reference toggle to compare against unconditional deformation.

## Legacy conversion

Run the one-time migration before opening legacy scenes/prefabs:

```sh
dotnet WolfEngine.Editor/bin/Debug/netcoreapp10.0/WolfEngine.Editor.dll --migrate-animation /absolute/project/path
```

It creates ordinary single-clip graph/set/wrapper assets under `Assets/Animation/Migrated`, preserves prior loop/speed/time/playback settings, and replaces legacy Animator fields. The conversion is idempotent. The POC runtime interface and public direct-clip playback path have been removed; a test-only sampler verifies equivalent single-clip output.

Root-motion application, retargeting, IK, additive poses and skeletal-key editing are future work.
