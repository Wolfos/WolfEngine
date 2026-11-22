# Mesh LOD

Status: proposal, not implemented.

WolfEngine draws every mesh at full detail at any distance. This document proposes a level-of-detail system for meshes: what a LOD is in the asset model, where the level is chosen, how it reaches the indirect draws, shadows and the ray-tracing scene, and the order to build it in.

It is designed for open-world foliage first, because that is where full detail costs the most. The same system must serve rocks and props, and later the foliage system's entity-less instances.

## Why

Measured in the Foliage project's `ForestStress` scene (250 beech trees, 3,000 grass clumps, 800 ferns, M-series Mac, 1045x575 scene target), median GPU milliseconds:

| Pass | With trees | Without trees |
| --- | --- | --- |
| Shadow Map | 4.97 | 0.38 |
| GBuffer | 1.31 | 0.17 |
| DDGI Final Gather | 2.53 | 0.42 |
| Reflections (Ray Traced) | 2.10 | 0.29 |
| DDGI Probe Trace | 1.40 | 0.55 |
| Sum of all passes | 17.85 | 6.56 |

The shadow map is the largest single cost. It renders full-detail alpha-tested trees into three cascades out to 160 m, and that is raster work LOD removes directly.

Ray tracing behaves differently. Removing alpha-tested foliage beyond 30 m from the camera saves only 0.55 ms of the 4.3 ms foliage costs the ray-traced passes. Inside a dense forest, rays stop in the nearby canopy. LOD is therefore not the main lever for ray-traced foliage; near-field representation is (see "Ray tracing" below).

## Goals

- Pick a level per instance per frame without touching the CPU per instance. Sync stays change-driven.
- Keep one level choice for every renderer of an object, so a tree's trunk, leaves and billboard switch together.
- Fit the existing indirect-draw model, including Metal indirect command buffers that bake a mesh's index range into each record.
- Make shadows agree with what the camera sees.
- Import the LODs that asset packs already author, with no hand work.
- Work for entity-less instances later, so the foliage system can reuse it.

## Non-goals for the first version

- Generating LODs by simplification. meshoptimizer is already linked, but foliage simplifies badly, so authored LODs come first.
- Generated impostors (octahedral or similar). An authored billboard mesh is the last level for now.
- Streaming LOD data in and out. All levels of a loaded mesh stay resident.
- Hierarchical LOD (merging many objects into one far mesh).

## Model

### A mesh owns its levels

`Mesh` gains an ordered list of levels. Level 0 is the full mesh as it is today; level n is coarser. Each level is a complete mesh in its own right, with its own vertices, indices, bounds and shadow index stream, packed like any other mesh. A mesh without authored levels has exactly one.

This keeps `MeshRenderer`, prefabs and scenes unchanged: a renderer still references one mesh asset, and a mesh with levels simply draws coarser further away. It is also what an entity-less foliage instance needs, a reference to one mesh.

The alternative, separate mesh assets per level tied together by a component (Unity's LODGroup), makes every level a renderer and every switch an enable toggle. That needs the CPU per instance, which does not scale to foliage.

### A LOD group decides for several renderers

Most objects have one renderer and need nothing more: the mesh's own switch distances apply, measured on that instance's bounds.

An object made of several renderers needs one decision for all of them, or its parts switch at slightly different distances. A tree is the common case: trunk, leaves, and a billboard that replaces both. For that, a `LodGroup` component on the object's root holds:

- the switch thresholds for the whole object, one per level transition;
- the bounds the decision is measured on, covering every renderer.

Each renderer under a group gets a level range, the group levels it draws in. The NatureManufacture beech tree becomes:

| Renderer | Mesh levels | Draws at group levels |
| --- | --- | --- |
| Trunk | 3 (LOD0 to LOD2) | 0 to 2 |
| Leaves | 3 (LOD0 to LOD2) | 0 to 2 |
| Billboard | 1 | 3 |

A renderer draws its mesh level `groupLevel - firstLevel`, clamped to its last level. Outside its range it is not drawn.

### Switching metric

Thresholds are screen-relative size: the projected diameter of the group bounds as a fraction of the screen height. This is the metric Unity's LODGroup uses, so imported thresholds carry over. It does not depend on resolution, and it responds to field of view, so a zoomed camera keeps detail.

A global LOD bias in the render config scales every threshold, for quality settings and for performance captures.

## Selection on the GPU

The cull shader (`Shaders/GpuDraw/gpu_draw_cull.compute.slang`) already runs one thread per instance and reads the instance's bounds. It also picks the level:

1. Compute the screen-relative size of the LOD bounds from the LOD reference view.
2. Compare it with the instance's thresholds to get the group level.
3. Map the group level to the renderer's mesh level, or skip the instance if the renderer is out of range.
4. Append the instance to the batch for that level, as it appends to a batch today.

### Batches per level

A batch draws one mesh with one execution lane, so its indirect record and, on Metal, its ICB entry can bake that mesh's index range. Each mesh level is therefore its own batch, and an instance is a member of one batch per level it can draw. Its visible-list capacity is reserved in each of those batches.

Per instance, the cull needs to find those batches. `GpuDrawCommand.batchIndex` becomes an index into a level table, holding:

- up to a fixed number of batch indices, one per mesh level;
- the threshold set;
- the renderer's level range.

Instances that share a mesh, lane, group shape and thresholds share one entry, so the table stays small.

Costs:

- Visible-list memory grows with the number of levels (one `uint` per instance per level).
- The cull reads one more small structure per instance.
- Indirect records are unchanged in kind; there are just more batches. Records are still encoded once when a batch is created, so a level switch costs no CPU work and no re-encoding.

### Hysteresis

Without it, an object sitting on a threshold flickers between levels as the camera moves by millimetres. The cull keeps each instance's last level in a per-instance buffer, and only moves to a coarser level once the size is a small margin past the threshold. Finer is the same in reverse.

### Shadows and other views

Shadow cascades use the camera's level, not their own. Shadows then match what is drawn, and a shadow never pops independently of its caster. The shadow cull already runs in its own dispatch, so it is given the camera as its LOD reference view, along with an optional shadow LOD bias. A bias above one makes shadows coarser than the camera, which is where the shadow-map saving comes from.

Every other view culls with its own camera as reference.

### Transitions

The first version switches levels hard, with hysteresis. A dithered cross-fade comes next: for a short band around each threshold, the instance is appended to both levels' batches with a fade value, and each draw discards pixels by a screen-space dither pattern. That doubles draws inside the band only, and it reuses the alpha-clip shader variants that foliage already compiles. It is kept out of the first version because it touches every geometry shader that reads the visible list.

## Import

### Authored LODs

FBX and glTF files from asset packs mark levels by node name: `beech_tree_05_LOD0`, `beech_tree_05_LOD1`, `beech_tree_05_leaves_LOD2`. The importer groups sibling nodes whose names differ only in a `_LOD<n>` suffix into one mesh with levels, ordered by n. Any other node imports as today.

The meshes of one group are separate Assimp meshes with their own vertices. Each level is optimised with the existing `MeshOptimization` and `ShadowMeshOptimization` passes.

Thresholds come from the model's import settings, defaulting to a halving of screen size per level. A source that carries its own (a converted Unity LODGroup) overrides them.

### LOD groups

When a model has several level-grouped meshes, the importer adds a `LodGroup` to the model root and level ranges to its renderers. A billboard (or any other last level) authored as a separate model is added to the group by hand, or by the Unity converter from the LODGroup it reads.

### Unity conversion

The Unity asset converter maps a prefab's LODGroup to a `LodGroup`:

- its `screenRelativeHeight` values become thresholds;
- each LOD entry's renderers get the matching level range;
- `fadeMode` is read for when cross-fading exists.

## Authoring

A mesh is a sub-asset of its model (an FBX or glTF source), so its levels are configured in the Model asset editor, next to the existing import settings.

- **Levels come from the source file.** The importer builds them from `_LOD<n>` nodes. They are not assembled by hand in the editor.
- **Thresholds live in the model's import settings.** The Model asset editor shows each level-grouped mesh as a list of levels, with a screen-size threshold per transition, and a preview that can force a level and shows each level's triangle count. Thresholds only change GPU data, so the editor applies them without reimporting the model, unlike the import scale.
- **A generated `LodGroup` reads the same settings.** When the importer adds a `LodGroup` to a model root, for a tree whose trunk and leaves are both in one file, its thresholds come from those import settings. Editing them in the Model asset editor changes every placement of the model.
- **A prefab can override a `LodGroup`.** A prefab that needs different switch sizes, or adds a last level from another file such as a billboard, edits its `LodGroup` component like any other component. Prefab overrides already record which components a prefab changed, so an overridden group stops following the model's settings and the others keep doing so.

The Model asset editor is therefore the default place, and a prefab is the place for per-object exceptions.

## Ray tracing

The TLAS cannot change level per ray, and the ray-tracing scene is maintained on the CPU per instance. Its level is therefore chosen on the CPU:

- in a few coarse distance bands from the main camera;
- with hysteresis wide enough that a band change, which needs a TLAS rebuild, happens rarely;
- at a level bias of its own, because diffuse rays tolerate coarser geometry than the camera does.

Each level is its own BLAS, shared by every instance using that level.

The measurements above say distance-based levels recover only part of the ray-tracing cost. Near-field foliage is the larger part. That calls for an RT-specific representation of alpha-tested levels: an opaque cutout mesh that drops the transparent parts of leaf cards and keeps only a thin alpha-tested edge band. Generated at import, it becomes another per-level mesh used only for the BLAS. It is a separate project that builds on this one, and it is left out here.

## Phases

1. **Mesh levels and import.** `Mesh` holds levels; the importer groups `_LOD<n>` nodes; the editor and cooked content load a mesh's levels with it. Level 0 is drawn everywhere, and the levels are not uploaded until phase 2 draws them. Nothing visible changes. This lands the data model on its own.

   As built:
   - A level is an ordinary mesh sub-asset. Level 0's `.mesh.bin` (format version 4) lists its levels' node ids, finest first, and the pipeline records a hard `mesh-lod` dependency to each, so builds cook them.
   - Grouping (`ModelLodGroups`) leaves the source node list as it is, so the hierarchy keys that mesh ids derive from never move. Coarser level nodes, and the `$AssimpFbx$` pivot nodes Assimp inserted for them, are only left out of the model graph, so they no longer become entities.
   - A level placed differently from level 0 in the source is moved into level 0's local space at import.
   - Anything that cannot be grouped safely imports as before: skinned meshes, a level with a mesh that level 0 lacks, a missing level 0, or a mirroring transform between levels.
   - The 3D importer's version went to 16, so every model reimports once to pick up its levels.
2. **GPU selection.** The level table, batches per level, selection and hysteresis in the cull, camera reference for shadows, a LOD bias in the render config, and a debug view that colours instances by level. Validated in ForestStress and Bistro: Bistro has no LODs, so it must render identically.

   As built:
   - `GpuDrawBatches` puts an instance whose mesh has levels in the batch of every level, up to eight. `GetBatchLodTable` gives the cull, per level 0 batch, each level's batch and thresholds. `GpuDrawCommand` is unchanged apart from its padding word, which now holds the level the camera cull last picked, so hysteresis needs no extra buffer. The instance update shader keeps that word for the same instance and resets it for a new one.
   - The level table is bound to the cull at `u2`, the one register free on both backends: every SRV register D3D12's compute root offers shares a Metal buffer index with the cull's other bindings.
   - Visible lists have their own capacity, grown from the batches' total capacity a frame after it changes. Until then the cull skips any batch that would reach past its view's slice.
   - `RenderConfig.MeshLod` holds `Enabled`, `Bias`, `ShadowBias` and `Hysteresis`. The `set_mesh_lod` automation tool changes them in memory.
   - Thresholds default to 0.25 of screen height for level 1, halving per level, until phase 3 makes them editable.
   - Not built yet: the debug view that colours instances by level.
   - Measured in `ForestStress` (250 trees, 120 m across), from inside the forest: the shadow map went from 5.78 to 4.93 ms and the G-buffer from 1.36 to 1.10 ms. Most of that small forest sits within the default level 1 distance for a beech tree, about 75 m, so larger scenes save more.
3. **LOD groups.** The `LodGroup` component, level ranges on renderers, group bounds in the cull, and Unity LODGroup conversion. The beech tree switches as one, and reaches its billboard.

   As built:
   - `LodGroup` (thresholds, plus bounds in the entity's local space) and `LodGroupMember` (`FirstLevel`, `LastLevel`) are components. The mesh gather walks up from each changed renderer to its nearest group, moves the group's bounds into world space, and passes a `GpuDrawLod` with the renderer's draw. A change in membership rewrites the instance data like a transform change.
   - `GpuInstanceData` gained the group's world sphere and one packed word: grouped flag, level range, and threshold set index. Distinct threshold sets are stored as entries in the level table, after the batch entries, so the cull needs no new binding.
   - Every member of a group steps the group level from its own stored level with the same inputs, so members created together agree. A member re-created alone, for a mesh change for example, can disagree within the hysteresis band until the size leaves it.
   - The importer generates a `LodGroup` for a model with two or more renderers that have levels, with bounds enclosing every renderer. Placing the model adds the group to its root, or to the wrapper when the model has several roots.
   - `ModelImportSettings.LodScreenSizes` holds authored thresholds, fitted to each mesh's level count and written into level 0's `.mesh.bin` (format version 5). Unlike the Authoring section intends, changing them reimports the model. The Model asset editor does not show them yet.
   - Unity LODGroup conversion maps `screenRelativeTransitionHeight` directly, since it measures the same thing. Unity's last transition culls the object; WolfEngine has no culled level, so the last LOD stays drawn.
   - Importer version 17 regenerates models with their groups.
   - Measured in `ForestStress`, with the beech prefab converted from its Unity LODGroup (thresholds 2.1, 1.5 and 0.2995, billboard at level 3), LOD off to on: inside the forest 19.8 to 16.9 ms in total, with the shadow map 4.9 to 2.9 ms; from 95 m 13.7 to 8.8 ms; from 200 m 9.1 to 5.6 ms. Bistro, which has no groups, renders identically.
4. **Ray-tracing levels.** CPU band selection for the ray-tracing scene, with its own bias.
5. **Cross-fade.** Dithered transitions.

Generated LODs, impostors and RT cutout meshes follow as their own projects.

## Questions to settle before phase 1

- Mesh-owned levels with a group component for multi-renderer objects, as proposed, or a group-only model where every renderer lists per-level mesh assets?
- Screen-relative size as the metric, or plain distance? Distance is simpler to author and debug, but changes with field of view.
- Shadows at the camera's level plus a bias, as proposed, or each cascade choosing its own level?
- Is a hard switch with hysteresis acceptable for the first version, or must cross-fading ship with it?
