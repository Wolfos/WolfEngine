# Foliage

Status: phase 1 (placed foliage) built; phases 2 to 6 are proposals.

WolfEngine draws foliage as ordinary entities with mesh renderers. This document proposes a foliage system for open worlds: how foliage is stored without entities, how it reaches the GPU-driven draws, shadows and the ray-tracing scene, how it is authored, and the order to build it in. It builds on mesh LOD (see `mesh-lod.md`), which was designed with this system in mind.

## Why

The Foliage project's `ForestStress` scene has 250 trees, 3,000 grass clumps and 800 ferns as about 4,000 entities. A real forest needs two to three orders of magnitude more.

Entities stop scaling there, for three reasons:

- **GPU memory.** A `GpuInstanceData` record is about 224 bytes. A beech tree with trunk, leaves and billboard renderers is three of them, so 100,000 trees take about 67 MB before any visible lists.
- **CPU sync.** Every renderer is a `DrawRecord` in `GpuDrawDatabase`, found through dictionaries and touched by the gather. At hundreds of thousands of renderers that is real frame time, even when nothing changes.
- **The handle limit.** Draw handles have a 20-bit index, so the instance table holds at most about a million records. Grass alone exceeds that over a few square kilometres.

## Goals

- Hold 100,000 or more placed objects (trees, bushes, rocks) per terrain with no CPU work per instance per frame.
- Draw dense ground cover (grass, flowers, small plants) out to a configurable distance without storing it per plant.
- Reuse GpuDraw's batches, materials, passes and mesh LOD, including LOD groups, rather than a second renderer.
- Shadows and ray tracing that agree with what the camera sees, within per-type distance limits.
- Live editing: a brush stroke or a density change shows on the next frame.
- Convert the Unity pack's tree instances and detail maps.

## Non-goals for the first version

- **Runtime changes by gameplay**, such as chopping a tree. Instances keep stable ids, (cell, index), so a per-cell removal mask and promotion to an entity can be added later without a format change.
- **Streaming.** Data is laid out per cell so it can be paged later, but a terrain's foliage loads with the terrain.
- **More than one terrain.** Foliage belongs to the single terrain of a scene.
- **Detail foliage on meshes.** Ground cover grows on terrain only. Moss on rocks and plants on mesh cliffs are placed foliage.
- **Interaction**, such as grass bending around the player.
- **Generated impostors.** The last level of a type is its authored billboard, as in mesh LOD.

## Model

Foliage comes in two tiers. They differ in how instances come to exist, not in how they are drawn.

| | Placed foliage | Detail foliage |
| --- | --- | --- |
| Examples | Trees, bushes, rocks, large ferns | Grass, flowers, small plants |
| Typical count | 10⁴–10⁵ per km² | Millions per km², 100,000–300,000 near the camera |
| Stored as | An instance list per cell | Nothing per plant: biomes on terrain layers, plus painted overrides |
| Instances created | When authored | On the GPU every frame, near the camera |
| Ray tracing | Within a distance per type | Never |
| Collision | Planned (phase 5) | Never |

### Foliage type

A `FoliageType` is a data asset describing what a piece of foliage is, independent of where it grows:

- the prefab it draws, usually a model or prefab with a `LodGroup`;
- a cull distance, a shadow distance and a ray-tracing distance;
- for detail use, the distance over which it thins out and shrinks away.

Where and how densely it grows (scale range, normal alignment, slope and height limits) belongs to the placement that uses it: a brush for placed foliage, a biome entry for detail. One type can therefore be both a placed tree and, in a sapling biome, detail.

A type's prefab is flattened into a type record when loaded: its renderers, each with mesh, material and `LodGroupMember` range, and the group's thresholds and bounds. Renderers in the prefab must sit in its local space; anything else on the prefab (scripts, lights, colliders) is ignored for drawing.

### Terrain foliage asset

A terrain's foliage lives in its own binary asset, `TerrainFoliage`, referenced from `TerrainComponent` by a new `AssetRef<TerrainFoliage> Foliage` field. Keeping it out of `TerrainAsset` means a brush stroke does not rewrite the heightmap file, and the two can be versioned separately.

It holds:

- the foliage types it uses, as asset references, so instances store a small type index;
- a cell grid aligned to the terrain's chunk grid (`ChunkSizeMeters`, 64 m by default);
- per cell, its placed instances sorted by type, with a range and bounds per type;
- per cell, the detail override paint, stored sparsely: only cells and types that were painted.

### Placed instance

A placed instance is 16 bytes:

| Field | Encoding |
| --- | --- |
| Position | Three 16-bit unorm values across the cell's bounds, about 1 mm over 64 m |
| Rotation | A 32-bit packed quaternion (smallest three) |
| Scale | A 16-bit float, uniform |
| Type | A 16-bit index into the asset's type list |

100,000 trees are 1.6 MB. Foliage does not move, so there is no previous transform; motion vectors come from the camera, plus wind.

### Biomes

A `FoliageBiome` is a data asset holding a list of detail entries. Each entry has:

- a `FoliageType`;
- a density, in plants per square metre;
- scale and tint ranges;
- clumping noise: scale and strength of a low-frequency noise that raises and lowers density, so grass grows in patches and flowers in drifts;
- slope and height limits;
- how far it aligns to the terrain normal.

`TerrainLayerDefinition` gains `AssetRef<FoliageBiome> Biome`. A layer without a biome grows no detail. Several layers can share one biome, and because density is blended by layer weight, two such layers give continuous coverage across their boundary: only the albedo changes there.

The entry list is typed, so a placed entry kind ("a beech every 12 m on this layer, never on slopes over 30°") can be added later for procedural placed foliage without changing the format.

## Detail density

At a point on the terrain, the density of a detail type is:

1. the sum, over the four layers in that layer-map texel, of layer weight times the type's density in that layer's biome;
2. times the entry's clumping noise;
3. cut by the entry's slope and height limits, evaluated from the heightmap;
4. replaced or scaled by override paint where the cell has any;
5. zero inside the footprint of placed foliage.

### Automatic layers

Layer index 0 in the layer map means "choose by slope", and the terrain shader resolves it per pixel. Detail generation must resolve it identically, or cliffs that render as rock grow the grass layer's biome. That selection moves into a shared Slang function called by both the terrain shader and detail generation.

### Override paint

Override paint is 8-bit density per type at 0.5 m per texel, about 16 KB per type for a painted 64 m cell. A brush either adds a type, removes a type, or clears all detail. Removal is stored as a multiplier, so a cleared path stays clear when its biome changes.

### Placed foliage exclusion

Each placed type has a footprint radius, so grass does not grow through rocks or trunks. The footprints are rasterised into an occupancy mask per cell when the cell loads and whenever its placed instances change. The mask is derived data and is not stored.

### Stable generation

Detail candidates sit on a jittered grid in world space, one grid per type. Each candidate has a fixed random value derived from its position, and is kept while that value is below the density there. As a result:

- raising density only adds plants, and lowering it only removes them; existing plants never move;
- distance thinning and the quality setting lower the same cutoff, so the same plants disappear each time and nothing shuffles;
- near the end of its distance a type also shrinks to nothing, so the last plants fade out rather than pop.

## Rendering

Both tiers produce compact instances, and one cull path draws them into GpuDraw's batches.

### Placed foliage

Each cell's instances are uploaded once when the terrain loads, and again when an edit dirties the cell.

The cull runs in two stages, for the camera and for each shadow view:

1. **Cells:** each cell's per-type bounds against the view and the type's cull distance (or shadow distance, for shadow views).
2. **Instances:** an indirect dispatch over the instances of surviving cell ranges. Each instance is tested against the frustum, picks its group level with the phase 3 `SelectLodLevel` on its type's bounds and thresholds, and appends one visible entry per renderer whose `LodGroupMember` range covers that level, into that renderer's mesh-level batch.

If measurement shows a flat pass over all instances is cheap enough at the target counts, phase 1 can ship the instance stage alone and add the cell stage when it pays.

### Detail foliage

Once per frame, a compute pass walks the cells within the longest detail distance of the main camera. It evaluates density at each candidate, and writes the survivors as compact instances into a transient buffer with a fixed budget. The same cull then draws them for the camera and the shadow views. Generating once and culling per view keeps shadows consistent with the camera.

When the budget is full, further candidates are dropped, and the drop is reported in GPU draw diagnostics.

### Draw integration

Visible lists hold a `uint` per visible entry, the index of a `GpuInstanceData` record, which every geometry shader reads. Foliage entries set a high bit, and the shaders decode the transform from the compact instance and its type record instead. That touches every pass that reads instances: G-buffer, shadow map, and transparent forward for blended foliage, plus the ray-tracing hit shaders.

That decode path is the main cost of this design. In return, foliage needs about a seventh of the memory and no CPU work per instance.

Each foliage renderer and mesh level becomes a batch in `GpuDrawBatches`, created from the type records rather than from draw records. Its capacity is the number of instances of that type, for placed foliage, or its share of the budget, for detail.

### Shadows

Each type has a shadow distance, enforced by the cell and instance cull. Shadow views pick levels from the main camera, as in mesh LOD. Detail types default to no shadows, or to the nearest cascade only.

### Ray tracing

Placed foliage within a type's ray-tracing distance of the main camera enters the TLAS. The CPU keeps each cell's instances for this, and adds or removes a cell's TLAS instances when the camera crosses into a new set of cells, so the TLAS rebuilds rarely. TLAS instances of foliage use a range of instance ids that the hit shaders decode from the foliage buffers, the same way terrain chunks have their own mapping. Alpha testing works as it does today.

Phase 1 draws every included instance at level 0. Phase 5 picks coarser levels in distance bands, as mesh LOD's planned ray-tracing levels describe. Detail foliage never enters the TLAS.

### Wind

A global wind state (direction, strength, and a scrolling gust texture) goes into frame constants. A shared Slang function animates vertices from it: trunks sway by height, branches and leaves flutter by a per-vertex weight. Materials opt in with a wind mode (none, tree, grass) and a few parameters. How the NatureManufacture assets encode per-vertex weights, probably in vertex colour, is checked when wind is built.

Motion vectors need the previous frame's wind state, kept alongside the current one. BLAS stay static, so ray-traced reflections and GI see foliage at rest, which is accepted.

### Quality settings

`RenderConfig` gains a `Foliage` section: a detail density scale, a detail distance scale, and a placed cull distance scale. An automation tool sets them in memory for A/B captures and timings, like `set_mesh_lod`.

## Authoring

Foliage is edited in the terrain editor, which gets two new tabs.

### Placed foliage

- **Paint brush:** a list of types with weights, density per 100 m², minimum spacing, scale range, random yaw, normal alignment, and slope and height limits. Spacing rejects candidates near existing instances, which gives an even, natural spread.
- **Erase brush:** removes instances of the selected types, or of all types.
- **Undo:** a stroke records the cells it changed.
- **Following the ground:** instances store an absolute height in the terrain's local space. When a sculpt stroke ends, the instances under it move up or down by as much as the surface under each of them moved, so a deliberately sunk rock stays sunk. Their rotation is not re-aligned. That edit is part of the sculpt's undo step. **Snap to terrain** in the Foliage tool puts every instance on the surface, for foliage placed before the terrain was shaped or after its heightmap was replaced some other way.

Instances are not entities, so they do not appear in the hierarchy and cannot be selected one by one in the first version.

### Detail foliage

- **Biomes** are edited in their own asset editor, and assigned per layer in the layer set editor.
- **Override brush:** add or remove a type, or clear all detail.

Detail is regenerated every frame, so palette changes, brush strokes and layer repaints show on the next frame with no bake.

## Unity conversion

Unity terrain data stores trees and detail separately, which maps onto the two tiers:

- Tree prototypes become `FoliageType`s pointing at the converted prefabs, such as the beech prefab with its LOD group.
- `TreeInstances`, with normalised position, width and height scale, and rotation, become placed instances. Unity scales trees non-uniformly by width and height; placed instances scale uniformly, so height is used and the difference is reported.
- Detail prototypes become detail `FoliageType`s, and the per-type detail maps become override paint (add). The converted terrain starts with empty biomes and fully painted detail, and can move to biomes gradually.

The pack's demo terrain data (`Terrain Forest Demo Standard Source.asset`) is in Unity's binary serialisation, so conversion needs a reader for it. That reader belongs with terrain import, which this phase is built together with.

## Validation

- `ForestStress` is rebuilt as placed foliage from the same generator, and compared with the entity version: captures should match closely, and GPU timings and memory should drop.
- Bistro has no foliage and must render identically.
- Automation gains a foliage state query (types, placed instance count, detail instances generated and dropped) so tests can check counts without reading a capture.

## Phases

1. **Placed foliage.** `FoliageType`, `TerrainFoliage` and its reference from `TerrainComponent`, the compact instance, upload per cell, the type table with LOD groups, the cull into GpuDraw batches, the decode path in the geometry shaders, shadow distances, and ray tracing at level 0 within a distance. No editor tools: data comes from a script or an automation tool. Validated against the entity `ForestStress`.

   As built:
   - **Assets.** `FoliageType` and `TerrainFoliage` are data assets, and `TerrainComponent.Foliage` references the latter. `TerrainFoliage` stores each cell's packed instances as a byte array, which is base64 in its JSON, not a binary asset of its own. That keeps the existing data asset pipeline, editor and cooking. A binary format can replace it when painting or streaming needs one; it changes the file, not the runtime.
   - **Packing.** `FoliageInstancePacking` is the 16-byte form described above. Rotation components use 1022 steps rather than 1023, so zero is exact. With 1023 steps every yawed tree came back tilted by about 0.08 degrees, which moved leaves 20 m up by a pixel.
   - **Types.** `FoliageTypeRuntime` instantiates the type's prefab into a private world and reads its renderers, their transforms relative to the root, the root's `LodGroup` and each `LodGroupMember`. A prefab without a group uses its first renderer's mesh thresholds. At most eight renderers per type.
   - **Visible instances are expanded, not decoded.** The doc proposed decoding compact instances in every geometry shader. Instead, the foliage cull writes a full `GpuInstanceData` for each visible renderer into a region of the instance table past `InstanceCapacity`, and appends that index to the visible lists. No geometry or ray-tracing shader changed. Memory follows what is visible rather than what is placed: the region is sized from the scene's foliage up to 262,144 slots, about 58 MB at most, and is not created for scenes without foliage. Its counters (expanded, dropped) are reset every frame but not yet read back.
   - **Batches.** `GpuDrawBatches.Reserve` adds one slot per instance of a type to the batch of each mesh level a renderer can draw, keyed by lane and mesh like any instance, so foliage shares batches and draw records with entities using the same mesh. Alpha-blended materials are skipped, since blended batches hold one instance each.
   - **Resources.** Each type renderer keeps its mesh and material in the draw tables through a draw record with the new `GpuDrawRole.ResourcesOnly`, which is neither batched nor ray traced. The cull's renderer table takes the material handle and lane from it.
   - **Culling.** One thread group per chunk of at most 64 instances of a cell. Instances are reordered along a Z-order curve within their cell before chunking, so each chunk's bounds are small. A chunk's bounds and longest distances are tested first, then each instance's type bounds, cull or shadow distance, frustum and LOD. The per-type ranges inside cells were not needed. The group level is kept per instance per view for hysteresis, written by the camera cull as for entities.
   - **Ray tracing.** Instead of an instance-id range decoded in the hit shaders, instances within their type's ray-tracing distance get draw records with `GpuDrawRole.RayTracingOnly`: in the TLAS, never rasterised, level 0 renderers only. They are persistent and chosen again only when the camera has moved 4 m or the foliage changed, so a still camera costs nothing per instance.
   - **Diagnostics.** The `get_foliage_state` automation tool reports foliage sets, instances, batch reservations, reserved visible slots, the expansion region's capacity and the TLAS instance count.
   - **Fixed in passing.** `GpuDrawDatabase.NotifyMaterialChanged` dropped LOD group membership from the instance data, so a material edit ungrouped a tree's renderers until their transform changed.
   - **Validation.** `ForestStressPlaced` (placed) and `ForestStressTerrain` (entities) share a flat terrain and the same 250 trees, 3,000 grass and 800 ferns. With TAA, effects and LOD off, placed foliage matches an entity scene built from the same decoded transforms to 0.013% of pixels, the run-to-run noise. Against the original transforms 3.4% of pixels differ by more than 8/255, all sub-pixel leaf shifts from the stored precision. Bistro matches the mesh LOD baseline (0.07%).
   - **Measured** in ForestStress, on a laptop in low-power mode, so as ratios only: the foliage cull costs about what the entity cull does (camera 0.10 against 0.08 ms, shadows 0.26 against 0.21 ms from inside the forest). The CPU gather drops to 0.05 ms, the same as static entities. Ray-traced passes are cheaper because foliage beyond its ray-tracing distance leaves the TLAS: 3.1 against 4.7 ms for reflections from inside the forest.
   - **Not built or not checked:** reading back the dropped-instance counter; the D3D12 backend and cooked builds; foliage on a moving or scaled terrain beyond the uniform scale the cull assumes.
   - **Found, not fixed:** the entity path puts every renderer of a LOD group in the TLAS, including the billboard that only draws at level 3. Placed foliage leaves it out.
2. **Detail foliage.** `FoliageBiome`, the layer's `Biome` field, the shared automatic layer selection, GPU generation with clumping, limits, thinning and the budget, placed foliage exclusion, and override paint data (no brush yet).
3. **Wind.** Wind state, the vertex function, material wind modes, and previous-frame wind for motion vectors.
4. **Editor tools.** Placed paint and erase brushes, the detail override brush, the biome editor, and undo.
5. **Ray-tracing levels and collision.** Levels by distance band for foliage in the TLAS, and Jolt static bodies for placed instances near physics observers.
6. **Unity conversion,** with terrain import.

## Decisions taken without discussion

These were defaulted while writing; change them before phase 1 if they are wrong.

- Placed foliage belongs to the terrain, in an asset the terrain references, rather than to separate foliage volumes that could also scatter on meshes.
- Wind comes after detail foliage, not before.
- Ray tracing at level 0 is part of phase 1, so DDGI and reflections do not lose the forest when it moves off entities.
- Detail instances are generated once per frame from the main camera and culled per view, rather than generated per view.
