# Animation graphs

Every animator evaluates a graph, including ordinary single-clip playback. Gameplay C# owns movement, facing, action selection, damage timing and projectile creation. Graphs compose presentation poses; they cannot call gameplay code.

## Authoritative assets

| Source | Purpose |
| --- | --- |
| `.animgraph.json` | Typed parameters, pose nodes, constrained state machines, output and separate canvas layout. |
| `.anim.json` | An authored clip: skeleton reference, frame rate, length, loop flag, bone keys on whole frames, named scalar curves and timed presentation markers. Imports to the same runtime clip as an FBX take. |
| `.data.json` (AnimationSet) | Rig reference and named clip slots, each containing a clip reference and playback-speed multiplier. |
| `.data.json` (BoneMask) | Rig reference and weighted bones, optionally including descendants. |

These files live under `Assets`. References use persistent metadata GUIDs. The asset pipeline records dependencies and produces Library artifacts. Edit curves and markers in the Clip Inspector. Authored clips store them in `.anim.json`; imported takes store them in their source `.meta` import settings under `AnimationClips`, keyed by the persistent sub-asset key (for example `animation:0`). Both compile into `AnimationClip`, so FBX reimport replaces motion keys while preserving annotations. Playback speed belongs to each set slot and multiplies the graph node speed. Skeletal keys of imported clips are read-only; author or edit skeletal keys in an `.anim.json` clip with the clip editor.

For animation-only FBXs, set **Animation skeleton** in the model importer before reimporting. Binding validates joint names, ancestry, invariant joint offsets and units against the target. The target rig supplies the bind pose; the animation-only export's first animated frame is not treated as a new bind pose. Genuine entity-transform channels remain entity channels. Explicit ignored-channel settings handle known export defects and should be used narrowly. Imported skinned meshes preserve their full bind transform into rig coordinates. Rendering converts the shared rig palette into each mesh's bind coordinates for current and previous poses; sockets continue using rig coordinates.

## Migration

Opening a project upgrades version 1 and 2 animation sets to version 3. Each sequence reference becomes an inline `{ Clip, PlaybackSpeed }` entry. Set asset IDs and slot names are preserved, so scenes, prefabs and graphs keep their references. Curves and markers move into the referenced authored clip or imported take's source metadata. When wrappers attached different timelines to the same imported take, migration creates additional clip sub-assets in that source, preserving the original motion keys and each timeline without standalone wrapper files. These definitions live in `AnimationClipVariants` in the same import settings. The obsolete sequence files and their metadata are removed after the new sources are written. Legacy standalone `.animset.json` and `.animclip.json` sources are supported too.

Migration checks all sources before writing and refuses missing sequence references, missing annotated clip sources, or different timelines attached to the same authored clip (duplicate that authored clip first). Fix those conflicts before reopening; it never silently chooses one timeline. Reopening an already upgraded project makes no further source changes.

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

## Animate workspace: the clip editor

The **Animate** workspace edits skeletal keys with forward kinematics. Its panels are **Clip Viewport**, **Bones**, **Clip Inspector** and **Timeline**; they share one open clip, one selected bone and one playhead.

Open a clip with **Open in Animate** in the Asset Editor, or by selecting a clip in an Assets panel while Animate is showing. Authored `.anim.json` clips are editable. Imported clips open read-only; use **Duplicate as Editable Clip** to copy one into an `.anim.json` next to its source. Tracks whose keys all sit on frames and interpolate linearly or in steps are copied as they are. Any other track is resampled to a linear key on every frame, so mocap duplicates are dense.

Create a clip with **Create > Animation Clip** in Assets, or **New Clip...** in the Clip Inspector. A clip is bound to a skeleton, which decides the bones shown; pick it in the Clip Inspector. Selecting a skeleton or model before creating a clip in Assets pre-selects its skeleton. The preview model defaults to the model imported with the skeleton; choose another model or prefab in the inspector. Retargeting is not supported: tracks bind by bone name.

Clips store keys on whole frames, interpolate linearly (normalized lerp for rotation) and hold one key per bone per frame covering position, rotation and scale. Untracked bones keep their bind pose. Property tracks round-trip unchanged but are not edited here.

### Posing and keying

- Click a joint or bone in the viewport, or a bone in Bones or Timeline, to select it. A bone segment selects the bone it ends at.
- The gizmo moves, rotates and scales the selected bone (**W**, **E**, **R**), in local space by default. The Clip Inspector edits the same local transform as position, Euler degrees and scale.
- With **Auto Key** off, an edit is unkeyed: the bone shows orange, and moving the playhead or playing discards the edit. **Set Key** (**K**) keys the bone's current transform on the current frame.
- With **Auto Key** on, every edit writes a key. A gizmo drag or one field interaction is a single undo step.
- The viewport orbits with the right mouse button, pans with the middle button, zooms with the wheel and frames the selection with **F**.

### Timeline

The transport plays, pauses, steps and scrubs. **Space** plays or pauses, the arrow keys step a frame, and **Home** and **End** jump to the ends whenever a clip panel has focus. The ruler scrubs. Frame rate, length and loop are set in the Clip Inspector. Changing the frame rate keeps keys at the same time and snaps them to the new frames. Shortening the clip asks before deleting keys past the new end.

The dopesheet lists a **Summary** row and the skeleton hierarchy. A folded bone shows its subtree's keys on one row, and **Keyed only** hides bones with no keys below them. A diamond on the Summary or a folded row stands for every key on that frame beneath it.

- Click a diamond to select it. **Shift** adds and **Ctrl**/**Cmd** toggles. Drag across empty space to box-select.
- Drag the selection to move it. Hold **Ctrl**/**Cmd** while dragging to copy it instead. Keys cannot be moved past either end of the clip.
- **Delete** removes the selected keys. **Ctrl**/**Cmd**+**D** copies them so the first lands on the playhead, or straight after the selection when the playhead is already on its first key.
- **Ctrl**/**Cmd** + wheel zooms the dopesheet, **Shift** + wheel pans it, and **Fit** shows the whole clip.

### Saving and undo

The clip editor keeps its own undo history. While one of its panels has keyboard focus, **Ctrl**/**Cmd**+**Z**, redo, **Ctrl**/**Cmd**+**S**, **Delete** and **Ctrl**/**Cmd**+**D** act on the clip and never on the scene. Saving writes the `.anim.json` and reimports it, so scenes using the clip update. Opening another clip or closing this one asks to save, discard or cancel when there are unsaved edits. Unsaved edits survive switching workspaces, but not quitting the editor.

## Animation Graph workspace

Open a graph from Assets to use the **Animation Graph** workspace (called **Animation** before the clip editor existed; an unrenamed workspace is renamed once). Edit clip sets, clip metadata and bone masks as DataAssets in the **Asset Editor**. The default workspace contains only the Animation window. Existing customized layouts and the active workspace are preserved; the former four-panel default is migrated. Rename/delete/save workspaces normally.

The Animation window has draggable dividers between graph and preview and between canvas and node inspector. The graph starts with most of the space. The Animation window provides a node canvas with middle-button pan, wheel zoom, node dragging, right-click creation/deletion menus and pose connections. The graph name follows the source filename; rename it in Assets. Connect an output button to a pose-input button, or choose inputs in the selected-node inspector. State-machine inputs can be inspected as subgraphs. Undo/redo preserves node IDs and layout. Save writes authoritative JSON through the normal asset refresh path.

Select a preview model or curated prefab and a clip set. The isolated preview uses the runtime evaluator with play/stop, step, speed and scrub controls, parameter editing and evaluation inspection. Scrubbing emits no markers. Invalid edits retain the last valid compiled preview and show a diagnostic. Hidden previews stop advancing and submitting render views. Closing the project/window releases the preview view and retires its private deformed geometry. Scene animators can be inspected read-only.

The Clip Inspector supports named curves with constant (step), linear and cubic Hermite interpolation, tangent editing, key insertion/removal and timed named markers. Imported annotations support save, undo and redo while the motion keys remain read-only. Imported skeletal keys remain read-only.

## Frame order and scheduling

Gameplay writes inputs, animation samples once per shared subgraph, resolved outputs/sockets are applied, transforms propagate, then rendering snapshots the result. `AdvanceClock` and `EvaluatePose` separate logical time from pose sampling for later crowd scheduling. Marker intervals account for skipped samples and loop boundaries. Exited state branches stop delivering markers; interrupted transitions capture their current blended pose.

Pose generations increment only when skeletal matrices change. Skinning dispatches and skinned BLAS updates stop for unchanged poses; rendered-pose history still settles motion vectors after a change. Set `WOLF_FORCE_SKINNING_UPDATES=1` or use the automation reference toggle to compare against unconditional deformation.

## Legacy conversion

Run the one-time migration before opening legacy scenes/prefabs:

```sh
dotnet WolfEngine.Editor/bin/Debug/netcoreapp10.0/WolfEngine.Editor.dll --migrate-animation /absolute/project/path
```

It creates ordinary single-clip graph/set/wrapper assets under `Assets/Animation/Migrated`, preserves prior loop/speed/time/playback settings, and replaces legacy Animator fields. The conversion is idempotent. The POC runtime interface and public direct-clip playback path have been removed; a test-only sampler verifies equivalent single-clip output.

Root-motion application, retargeting, IK and additive poses are future work. Clip editing has no curve editor, tangents, mirroring, copy/paste between bones, multi-bone posing or onion skinning yet.
