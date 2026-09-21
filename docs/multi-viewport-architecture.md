# Multi-viewport architecture

Design of record and handover for making the renderer support several 3D viewports rendering **in the same
frame**. The immediate consumer is a prefab editor, but nothing here is specific to prefabs: a material
preview, an animation scrubber or a second camera angle all need the same thing.

Views that are not visible are skipped as an optimisation, never as a correctness requirement.

## Handover status

Stage 0 is complete. Stage 1 is underway, but nothing user-visible has changed yet: there is still exactly
one recorded view. Every piece of persistent per-view state now lives per view, and the frame-resource bundle
has been split into `RenderViewResources` and `RenderFrameSharedResources`. Recording has matching shared
preparation, per-view, and shared presentation phases. The remaining structural step is to migrate the
render-thread consumers to per-view snapshots and loop the live submissions. `FrameSnapshot` now owns active
`RenderViewSnapshot` entries with isolated scene packets, draw databases and camera history; the old members
delegate to the primary entry until render-thread consumers migrate. `RenderPipeline.PublishSnapshot` now
also accepts the planned list of `RenderViewSubmission` values, gathers each bound view's world into its own
entry, and refreshes every database's shared handle-generation tables after all gathers. The standalone
runtime has migrated to that API; the editor remains on the compatibility publisher until its editor overlay
world is folded into the document world. Snapshot entries also track a binding generation, so reusing a
view slot does not carry the prior world's draw records or camera history into its replacement.

Landed in the `WolfEngine` submodule, commits `b0d19a3` through `d855a4f`:

- `World.Id`, and `DrawRecordKey` widened to `(WorldId, Entity, SubdrawId)`. `GpuDrawDatabase.BeginWorld`
  opens the world scope a gather's draws are keyed under.
- `RenderViewId`, a slot identifying one view. `RenderViewState` holds everything a view carries across
  frames. `RenderViewRegistry` owns those states and enforces one-world-per-view.
- `IRenderViewHost` on `RenderGraph`: `CreateView`, `DestroyView`, `TryGetViewTexture`,
  `TryGetViewForWorld`, `Views`.
- `UiTextureIds` viewport sentinel block, one per view, resolved per view on the render thread.
- `EditorViewportStateBus` keyed by `RenderViewId`, with the parameterless members delegating to the primary
  view so existing call sites still work.
- Per-view projection: `Camera.GetPerspective(size)`, resolved on the render thread and published on
  `SceneViewportRenderState` so gizmos, picking and rays use the same value the image was rendered with.
- `RenderViewOutput` (texture or backbuffer) as a per-view property.

The first Stage 1 slice also makes pass dependencies explicit: pass config builders that sample the shared
sky chain receive `RenderFrameSharedResources` separately, and a regression test prevents presentation and
sky handles from drifting back into `RenderViewResources`.

## The contract

A viewport is a world plus a camera, and it yields a texture. That is the whole public surface:

```csharp
public interface IRenderViewHost
{
    RenderViewId CreateView(in RenderViewDescriptor descriptor);
    bool DestroyView(RenderViewId view);
    bool TryGetViewTexture(RenderViewId view, out nint textureId, out Int2 size);
    bool TryGetViewForWorld(World world, out RenderViewId view);
    IReadOnlyList<RenderViewId> Views { get; }
}

/// <summary>Fixed for the life of the view. A world may back at most one view.</summary>
public readonly record struct RenderViewDescriptor(
    World World,
    string Name,
    RenderViewOutput Output);
```

There is deliberately no `ConfigureView`. A view's per-frame settings — visible, size, resolution scale,
debug view — already travel on `EditorViewportStateBus`, which the panel owning the viewport republishes every
frame. A second channel carrying the same values would be state to keep in sync rather than state to read.

A `RenderViewId` is a **slot, not a serial number**. The UI sentinel block is indexed by it and is
deliberately small, so destroying a view hands its slot to the next view created. That is why destroying a
view must clear its state: an id held past the destroy resolves to whatever view took the slot.

Per frame the pipeline takes one submission per view instead of one camera. The editor retains the old
single-camera-and-world-list overload until its document world replaces the overlay-world arrangement:

```csharp
void PublishSnapshot(IReadOnlyList<RenderViewSubmission> views);

public readonly record struct RenderViewSubmission(
    RenderViewId View,
    Camera Camera,
    WorldTransform CameraWorldTransform,
    RenderConfig Config);
```

`RenderViewOutput` is either a texture the caller samples, or the backbuffer, and it is a property of the
view rather than of the process. The standalone game is one view whose output is the backbuffer; the editor is
N views whose outputs are textures. `RenderPresentationOptions` now only describes how the *primary* view
starts, rather than deciding for every view at once.

## Model

**One world per view.** A view owns the world it renders, a camera, a target size and resolution scale, a
quality tier, a debug-view selection, an output, and **all persistent GPU state derived from that pairing**:
temporal anti-aliasing and FSR3 history, volumetric fog froxel history, the colour pyramid, shadow cascades,
the DDGI probe volume, and the top-level acceleration structure. Creating a view registers the world;
destroying it retires that state.

Above the views sits a process-wide shared layer keyed by content rather than by view: the GPU draw tables and
their handle registry, mesh and material slots, per-mesh bottom-level acceleration structures, and the skybox
environment, irradiance, prefilter and BRDF chain for a given sky configuration. Two views that draw the same
mesh share its slot and its BLAS. Only the TLAS is per view, which is cheap.

`ViewportHost` is the editor-side panel for one view. It owns the `RenderViewId`, the editor camera entity
inside that view's world, its own gizmo, picking and camera-movement state, and its own
`SceneViewportUiState`. **Not built yet** — stage 3.

`EditorDocument` is what a viewport shows: the open scene, or a prefab. It owns the `EditorScene` — and
therefore the world — plus a dirty flag, an undo history and a selection. **Not built yet** — stage 3.

Because a world backs exactly one view, `EditorDocument`, `World`, view and `ViewportHost` are one to one to
one to one. There is no scene-to-view fan-out to reason about.

### What this model gives up

Two viewports cannot show the same content from different angles without duplicating that content into a
second world, which is not viable for a large scene. Under one world per view that feature is out of scope.

It is the right trade. Everything that makes a shared world awkward is per view: `SelectionOutlineController`
writes `OutlineHighlight` components onto entities, gizmo and debug-primitive entities are created into the
world, and the editor camera is an entity in it. Two views over one world would need all of that keyed by view
inside the ECS. Duplicating a prefab or a preview world is cheap; sharing a scene world is not worth what it
costs everywhere else.

The escape hatch, if a second angle on one scene is ever wanted, is to allow several views per world. That
requires per-view overlay and selection state in the ECS, and it reopens the change-tracking problem described
next. Do not build it speculatively.

## Invariants

**A world backs at most one view.** `RenderViewRegistry.Create` throws on a world that already has one. This
is not bookkeeping; it is what keeps change tracking correct.

`DirtyWorldTransform.Consumed` and `WorldTransformRemoval.Consumed` are counters stored on the world. Each
consuming draw database increments them, and entries are pruned once every database has seen them — at
`DirtyWorldTransformSyncCount` of 3 and `WorldTransformRemovalSyncCount` of 2 in `RenderPipeline`. Those
constants are not tuning values. They encode the fact that `FrameSnapshotBuffer` holds exactly two snapshots,
each with its own `GpuDrawDatabase`, so a change is consumed twice and one further snapshot settles motion
history.

If a world were gathered by more than two databases in a frame, the counters would advance faster than the
thresholds assume and entries would be pruned before every database had seen them. The failure is silent:
draws go stale or vanish, with nothing logged. One world per view keeps the count at two, so the existing
protocol and both constants stay correct unchanged.

Per-world change epochs with a per-database watermark would remove the constraint entirely. That is the escape
hatch, not a prerequisite.

**Draw record keys carry world identity.** `Entity` is an index and generation unique only within one world,
while `GpuDrawHandleRegistry` draw slots and `GpuDrawTransformHistory` entries are keyed by `DrawRecordKey`
and shared across snapshots. Two worlds produce the same `Entity(5, 1)`, so without the world in the key their
draws collide: one view's transform history and handle ref-counts get attributed to the other's entity. This
was already latent before multi-viewport — the editor world and the scene world are gathered into one database
— and invisible only because the editor world contributes no `MeshRenderer`.

One shared registry and one set of GPU tables are kept, so the `MaxDrawCount`, `MaxMeshCount` and
`MaxMaterialCount` budgets stay global and mesh and material slots deduplicate across views.

With N views the shared registry is mutated by N gathers per frame on the game thread. Each database copies
the generation tables out of the registry at the end of its sync because the render thread reads them while
the other snapshot syncs. That copy must happen **after every view's gather**, or an early view's copy is
stale with respect to a later view that acquired new slots in the same frame.

`RefreshRenderWorlds` and `HasRenderWorldListChanged` should disappear in stage 1. A view's world is fixed for
the view's lifetime, so the full-reconcile trigger becomes "view created" rather than "world list changed".

## Per-view and shared frame state

Per-view cross-frame state lives in `RenderViewState`, owned by `RenderViewRegistry` and retired through
`device.Retire` when the view is destroyed. It covers the temporal history textures with their sizes, modes
and validity flags, the FSR3 luma and accumulation textures, the fog history grid, the colour pyramid, the
DDGI irradiance, visibility and probe-state textures with their lattice anchor and committed placement, the
frame-shape trackers that decide when history must be reset, and the view's output target, render size,
resolved projection and jitter-sequence shape.

The registry is shared by `RenderGraph` and `RenderGraphFrameBuilder`, because both need the same view's state
— the graph for its output target and render size, the builder for its history — and two copies would drift.

The former `RenderGraphFrameResources` bundle is split into:

- `RenderViewResources` — G-buffer, depth, motion vectors, ambient occlusion, lighting and post-processing
  targets. These are transient, and the resource registry already pools transient textures **by descriptor**
  rather than by name, with handles allocated from a counter. Declaring the same descriptor twice in one frame
  already yields two independent textures with independent state tracking, so no registry change is needed.
- `RenderFrameSharedResources` — the skybox chain, the GPU draw tables, the gameplay UI targets, the
  backbuffer.

`Build` then records shared preparation once and loops the views:

```csharp
RecordSharedPasses(graph, sharedResources);
for (var i = 0; i < views.Count; i++)
{
    RecordViewPasses(graph, views[i]);
}
```

## Snapshot structure

`FrameSnapshot` becomes a list of per-view entries, each carrying that view's draw database, lights, skinning
packets, decals, fog volumes, sun direction, config, camera and previous camera.

`SeedPreviousCameraFrom` must seed **per view**, not once per frame. A motion vector spans one frame and its
camera half has to line up with its transform half, which `GpuDrawTransformHistory` takes from the previously
published frame. A view that did not render last frame, or that was just created, has no history and resets
its own; it must not disturb another view's.

## Singletons: done and remaining

Each of these is a correctness bug under multiple views, not a limitation.

**Done — `Screen.CurrentResolution` and baked projections.** The global was written by the render thread from
the one scene render size, and `CameraResolutionUpdater` pushed it into every `Camera` and baked a projection.
With views of different sizes every viewport but one got the wrong aspect ratio, because a component can hold
only one projection. Projection is now resolved per view via `Camera.GetPerspective(size)`, against the size
the view is actually rendering at. The component keeps field of view and clip planes;
`Screen.CurrentResolution` stays for what it legitimately describes, the window.

The resolved projection must be **one** value shared by everyone who needs it. Gizmos, picking and viewport
rays build a view-projection too, and deriving theirs from the component while the renderer derives its own
from the view puts them on different aspect ratios, so they drift from the image they are drawn over. The
render thread publishes what it used on the per-view `SceneViewportRenderState`; the editor reads it through
`EditorViewportProjection.Resolve`, falling back to the baked projection only before a view's first frame.

Side effect worth knowing: the baked projection lagged a resize by a frame or more, because
`CameraResolutionUpdater` read a global the render thread had written on an earlier frame. Resolving against
the size being rendered this frame removes that lag, so resize behaviour improves rather than changes.

**Done — `EditorViewportStateBus`.** Now a registry keyed by `RenderViewId`, with `GetViews` and `RemoveView`.
The parameterless members address the primary view so the 19 existing call sites across 10 files still work.

**Done — `UiTextureIds.SceneViewport`.** Now a reserved sentinel block, one entry per view, each rewritten on
the render thread to that view's output. Both ImGui backends resolve any viewport sentinel to their fallback
texture when a view produced nothing.

**Remaining — bus consumers are still per-process.** `EditorCameraSystem`, `TerrainToolController`,
`TransformGizmoController`, `SceneSelectionController` and `GizmoLineRenderer` read the primary view's state
through the parameterless members. They become per viewport in stage 1 or 3.

**Remaining — `EditorCameraSystem` input state.** It keeps look delta, held movement keys,
`_hadViewportControl` and the last camera pose in its own fields, and drives every entity carrying
`EditorCameraMover` in a world, gated on the one bus. That state belongs to the `ViewportHost`, and only the
hovered or focused viewport may consume input. It also needs a pose setter, not only `TryGetCameraPose`, so a
viewport can frame its content when it opens.

## Cost

Most of a frame is per-view work: shadow cascades, culling, G-buffer, ambient occlusion, lighting, fog,
reflections, temporal resolve and post-processing. Concurrent views therefore cost close to linearly in GPU
time, and in memory, since transient pooling reuses textures across frames but needs every concurrently live
view's set within a frame.

The architecture's job is to let each view choose what to pay. Quality is per view:

- **Full** — every pass. The focused viewport.
- **Lite** — no temporal anti-aliasing, so no history textures are allocated at all; no DDGI trace; no
  volumetric fog, reflections or bloom.
- **On demand** — record the view only when it is dirty: its camera moved, or its world changed.

Defaults: focused is Full, visible but unfocused is Lite, and a view that is not its dock node's selected tab
or sits on an inactive workspace is not recorded at all. `SceneWindow.OnHidden` already expresses exactly that
condition for the single view by publishing `SceneViewportUiState.Hidden`; it becomes per view. In the common
case one view renders at full quality and steady-state cost stays close to today's.

## Editor documents and window binding

Per-document state is mandatory once several documents are open at once. None of this is built yet.

`IEditorInteractionState.IsSceneDirty` is a single bool, and `MarkSceneDirty(World)` decides whether to set it
by testing `world.Tag != WorldTag.Authoring`. A prefab world is also an authoring world, so a prefab edit
would mark the open scene dirty and a save would write the wrong file. Dirty state becomes per document,
resolved from the world that was edited — unambiguous now that a world belongs to one document — and
`EditorCommandService.SaveScene` becomes a save of the active document.

Undo histories become per document. Authoring-scope entries resolve their target through
`IEditorSceneWorkspace.CurrentScene`, which is not a prefab document's scene, and the existing guard that
suppresses authoring entries while play mode is active does not generalise: it would strand scene history
rather than isolate prefab history. The service holds one stack per document instead, pushed and popped with
the document, and `EditorUndoRedoContext` resolves its scene from the document.

Selection becomes per document. `EditorGui` holds it in statics today. Bookmarking and restoring selection
across a document switch, which `WolfEngineEditor` already does for the play-mode scene swap, is an acceptable
interim step; per-document selection state is the target.

Several live viewports raise a question the single-view editor never had to answer: which document does the
Entities window show? `EditorWindow.Draw(EditorScene)` becomes `Draw(EditorDocument)`, and each window gets a
binding mode shown in its header — **follow the focused viewport**, the default, or **pinned to a document**.
Without this, multiple viewports are ambiguous rather than useful.

Each document's world holds its own editor camera and overlay entities — gizmo lines, debug primitives,
selection outlines — created as editor-only entities and excluded from serialisation. The shared
`_editorWorld` goes away; there is nothing left for it to hold.

## Prefab documents

A prefab document is small once the above exists, but the load path has one decision that must not be got
wrong.

**Do not load a prefab for editing through `ProjectAssetPipelineService.InstantiatePrefab`.** It assigns fresh
entity ids and sets `EntityPrefabSourcePaths` on every entity, which is correct for placing an instance in a
scene and wrong for editing the prefab itself: every entity would become an instance of itself, and saving
would write new ids, breaking the link from every existing instance.

Load through `IEditorSceneReloadService.Restore` instead. It assigns `scene.EntityIds[entity]` from the saved
entity id, so identity survives a load and save, and it already merges nested prefab sources and remaps entity
references. Wrap the prefab file's entities in a snapshot holding a single global cell and no spatial cells.

Saving is the mirror: `Capture`, collect the root's subtree, clear the root's parent, and write the
`PrefabAssetFile`. Factor the atomic write and metadata handling out of `PrefabAssetCreator` into a
`PrefabAssetStore` shared by both paths, following `MaterialAssetStore` and `DataAssetStore`. Saving in place
must preserve the existing source id and sub-asset node id, or every save breaks the asset database's identity
for that prefab.

Two behaviours fall out of the loader choice without new code. The prefab's own entities become fully
editable, because `EditorPrefabUtility.IsNestedPrefabEntity` is false for them and the guards in
`EntitiesWindow` that block deleting or duplicating inside an instance do not fire. Nested prefab instances
*within* the prefab stay protected by those same guards, which is correct.

Two things a prefab world lacks and the document must supply. There is no light: the `Sun` is created into the
authoring world at editor startup. There is no `WorldSettings`, so the render config falls back to a default
and the prefab would not look as it does in a scene. The document creates both as a synthetic environment that
is tracked separately and never serialised.

`PrefabAssetFile` has one `RootEntityId`, so a prefab is single-rooted by format. The document enforces it:
new entities parent to the root by default, unparenting to root level is rejected, and a save reports any
entity outside the root's subtree rather than silently dropping it.

## Traps found while implementing this

Each of these cost real debugging or was caught only by looking closely. They are the parts most likely to be
reintroduced.

**`RenderGraph` and `RenderGraphFrameBuilder` each track the previous frame's anti-aliasing settings, and the
two pairs must stay separate.** They look redundant — both record whether temporal anti-aliasing was on and
which mode — but the builder writes its pair in `BeginFrame` while the graph reads its own later in the same
frame when it builds scene data. Collapsing them makes that later read see the value `BeginFrame` just wrote
for the *current* frame instead of the previous one, so the history reset never fires on a mode change and
temporal history silently carries across a switch that should have invalidated it. They are
`PreviousTaaEnabled`/`PreviousAntiAliasingMode` and
`SceneDataPreviousTaaEnabled`/`SceneDataPreviousAntiAliasingMode`, with a regression test that fails if they
are merged.

**`EditorSceneRenderTargetManager.Reset()` disposes immediately; `Release(device)` retires.** A view closing
while a UI frame still samples its texture needs the retiring path, which is the use-after-free that class's
own comment warns about. Per-view lifetime makes it reachable.

**The Halton jitter sequence still indexes off the renderer's global frame counter.** That is correct only
while every view is recorded every frame. Once views can be skipped, a view must carry its own sequence
position, advanced only on the frames it was actually recorded, or a skipped frame jumps its jitter and breaks
convergence. Belongs with on-demand recording in stage 2.

**Pass names must be qualified by view**, for example `$"GBuffer [{view.Name}]"`. DRED breadcrumb attribution
reports the enclosing pass name and `GpuProfiler` keys its scopes the same way, so duplicate names across
views make a device-removal log unattributable and merge unrelated timings. `RenderViewState.Name` exists for
this and is not used yet.

**Per-view constants must ride the existing per-pass constant buffer.** Metal has roughly 31 buffer slots with
27 to 30 taken by the bindless argument buffers, so a new persistent per-view buffer slot is not available.

**Several tests read renderer private fields by reflection.** `AntiAliasingRenderGraphTests` and
`VolumetricFogTests` reach into `RenderGraphFrameBuilder`, so moving state breaks them with a
`NullReferenceException` rather than a compile error. They now hop through the builder's `_view` field.
`WolfEngine.Tests` has `InternalsVisibleTo`, so prefer naming `RenderViewState` over stringly-typed
reflection.

## Validation

Two things are worth checking, and they need different tools.

**Unit tests.** `RenderViewTests` covers sentinel round-tripping and rejection outside the reserved block,
per-view bus isolation, `RemoveView`, debug-view overrides reaching views published before and after,
`Camera.GetPerspective` deriving aspect from the view size, and the registry refusing a second view over one
world plus slot reuse not inheriting released state. `FrameSnapshotGpuDrawTests` covers two worlds with equal
`Entity` values not sharing a draw slot or transform history. `AntiAliasingRenderGraphTests` covers per-view
history isolation, `ReleaseView`, and the two-pairs trap above.

**Frame capture.** Capture is not bit-deterministic, so "it renders identically" is not a claim this harness
can support. Bistro has temporal anti-aliasing and DDGI turned off to reduce the noise; with them on, two runs
of identical code differed by around 1600 pixels. With them off the same comparison gives roughly 90 to 220
pixels, of which about nine in ten differ by a channel delta of three or less — ordinary floating-point and
GPU-scheduling noise. The rest is a handful of isolated pixels swinging by more than 150, because screen-space
reflections and ambient occlusion are still on and screen-space ray marching next to a geometric edge behaves
exactly like that. Turning those off too would tighten the floor at the cost of no longer exercising passes a
per-view refactor has to keep working, so they stay on.

**Three runs per build, and compare ranges, not pairs.** The same-build spread is wide enough that a single
pair lands anywhere in it, making a change look clean or suspicious by luck.

```sh
for n in 1 2 3; do
  dotnet run --project WolfEngine.Editor/WolfEngine.Editor.csproj -- \
    --project /path/to/WolfEngineGame \
    --scene Assets/Scenes/Bistro/Bistro.scene.json \
    --frames 20 \
    --capture Artifacts/visual/<task>/after$n.png \
    --quit
done
```

`--capture` resolves relative to the **game project** root, not the working directory, so captures land under
`WolfEngineGame/Artifacts/visual/<task>/`. Keep them out of commits.

```sh
python3 scripts/capture-noise-floor.py \
  --before base1.png base2.png base3.png \
  --after  after1.png after2.png after3.png
```

The script prints the within-build spread for each set, the across-build range, and a verdict. A change is
clean when the across-build range sits inside the within-build spread on both pixel count and maximum delta.
It also reports whether the tightest cross-build pair beats the tightest same-build pair — the strongest
signal available, since a real difference cannot make two builds agree more closely than one build agrees with
itself — and whether any high-delta pixel appeared that the noise floor does not already produce.

For a pure rename or move, a stronger check than capture is available and was used for the 245-site state
extraction: reverse the rename mechanically and diff against the original, so anything left over is a change
that was not part of the move. Extract moved method bodies from both files, normalise, and diff those too.

The Stage 1 resource split was checked against the final Stage 0 `proj1`–`proj3` captures with three new
`resources1`–`resources3` captures. Same-build spread was 104–218 pixels before and 118–131 after; cross-build
spread was 70–219, one pixel above the strict baseline ceiling. The tightest cross-build pair (70 pixels) was
better than the tightest same-build pair (104 pixels), maximum delta stayed at the baseline's 67, and no new
high-delta pixel appeared. Treat this as noise-floor equivalent; the script's literal verdict is `OUTSIDE`
only because of the 219-versus-218 upper bound.

The per-view publisher was checked with three `publisher` captures against `resources1`–`resources3`.
Same-build spread was 36–229 pixels across the new runs; cross-build spread was 105–238, 9 pixels above
the strict ceiling. Maximum delta was 67 in both groups, no new high-delta pixel appeared, and the tightest
cross-build pair (105 pixels) beat the tightest baseline pair (118 pixels). The script's literal verdict is
`OUTSIDE`; this is reassuring but not a strict noise-floor pass.

Once two views exist, the capture diff stops being the right check. The new one is two views rendering
different worlds at different sizes with temporal anti-aliasing on: move one camera and assert the other
view's image is unchanged.

Automation cannot see a second view — `capture_frame` reads back the single scene colour target. Stage 1
should add `list_render_views`, `get_render_view_state(view)`, `capture_render_view(view, path)` and
`set_render_view_quality(view, tier)` to `WolfEngine.Editor.Automation`.

## Remaining work

**Stage 1 — two views on two worlds, rendering in the same frame.** No document work; proves the machinery.

1. **Done:** split `RenderGraphFrameResources` into `RenderViewResources` and
   `RenderFrameSharedResources`. Pass config builders now declare shared sky/presentation dependencies
   separately, and resource-ownership tests enforce the boundary.
2. **In progress:** `Build` now has shared-preparation, per-view, and shared-presentation recording phases.
   It still invokes the per-view phase once; loop it after snapshots carry several view entries.
3. **In progress:** `FrameSnapshot` now exposes an ordered list of active `RenderViewSnapshot` entries, with
   isolated scene packets and draw databases, and `SeedPreviousCameraFrom` seeds camera history by
   `RenderViewId`. `PublishSnapshot` accepts view submissions and gathers their bound worlds independently;
   the runtime uses it and the primary-view facade keeps the editor working. Pass contexts now expose their
   own `RenderViewSnapshot`, and material changes reach every active view database. Next, migrate the
   remaining render-thread setup off the frame facade and record
   several views in one graph. Migrate the editor publisher when its overlay world no longer needs the legacy
   multi-world gather.
4. Qualify pass names by view.
5. Drop `RefreshRenderWorlds` and `HasRenderWorldListChanged`; the reconcile trigger becomes "view created".
6. Per-view bus consumers and per-viewport editor camera input.

**Stage 2 — make it affordable.** Quality tiers, on-demand recording, skipping views that are not visible, and
the per-view jitter sequence position that on-demand recording requires.

**Stage 3 — `EditorDocument`.** Per-document dirty state, undo and selection, and window binding modes.

**Stage 4 — prefab documents.** Load, save, prefab viewport, and opening a prefab by double-click from the
asset browser.

**Stage 5 — nested prefabs.** Breadcrumbs, and opening a prefab from an instance in the hierarchy.
