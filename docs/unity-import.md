# Unity scene import

Status: terrain, terrain layers, terrain trees, directional lights and the main camera import. Prefab placements are exported but not imported yet, and models, materials and prefabs are still converted separately.

## Why an exporter

Unity can describe its own scenes better than anything outside it can. Its terrain API hands over heights, layer weights and trees directly. Its scene API resolves nested prefabs, overrides and parent transforms into final placements. Reading the files instead would mean a reader for Unity's binary terrain format and for its YAML scene format, including prefab resolution. So conversion is split in two:

1. **Export, in Unity.** `WolfEngine.UnityExporter` is an editor-only Unity package. Add it to a Unity project by path (`"com.wolfengine.unity-exporter": "file:/path/to/WolfEngine.UnityExporter"` in `Packages/manifest.json`). It writes a neutral folder in Unity's own space, referring to Unity assets by path.
2. **Import, in WolfEngine.** `UnitySceneImporter` (`WolfEngine.Editor/UnityImport/`) reads that folder and creates WolfEngine assets and a new scene.

Running the export needs Unity and a Unity licence. Anyone moving a project off Unity has both; it does mean the export cannot run in CI.

## Running it

Export from **Tools > WolfEngine > Export Open Scene...** in Unity, or in batch mode:

```sh
Unity -batchmode -nographics -projectPath <unity project> \
  -executeMethod WolfEngine.UnityExporter.WolfSceneExporter.ExportFromCommandLine \
  -wolfScene "Assets/Path/Scene.unity" -wolfOutput <folder> -logFile <log>
```

Import with the `import_unity_scene` automation tool. It takes the export folder, the Unity project (textures are read from it), a target folder under `Assets`, and a new scene name. It returns what was imported, skipped and guessed.

## The export folder

`scene.json` holds format version 1:

- **Terrains:** position, size, map resolutions, layers (textures, tiling, remaps), tree prototypes (prefab paths), and trees. Tree positions are 0 to 1 across the terrain; each tree has a rotation about Y and separate width and height scales.
- **Prefab instances:** the outermost prefab instance roots, with their prefab path and world transform. Each one also records how many overrides it has beyond its root transform, and of what kind.
- **Loose mesh objects, lights and cameras,** with world transforms.

Beside it, per terrain:

- `terrain_N_heights.r16`: 16-bit heights, row-major, rows along +Z.
- `terrain_N_alphamaps.u8`: one byte plane per layer, in the same row order.

Detail prototypes are not exported. WolfEngine generates detail from terrain layers (see `foliage.md`).

## Conversion

- **Space** (`UnitySpace`):
  - Both engines are left-handed, +Y up, +Z forward, in metres, so world positions carry over. So do the rotations of lights and cameras.
  - Models are imported differently. Unity mirrors an FBX's X axis and WolfEngine's importer mirrors Z, so a WolfEngine model is its Unity counterpart turned a half turn about Y. A placement of a model gets that half turn first.
- **Terrain:**
  - A Unity terrain spans from its position along +X and +Z. A WolfEngine terrain is centred on its entity, so the entity goes at the Unity terrain's centre.
  - The heightmap carries over sample for sample, with the height scale set to the Unity height range.
  - Each layer map texel keeps its four heaviest Unity layers, the most a WolfEngine texel holds. The import reports the mean weight dropped; on the Forest demo it is zero.
- **Layers:**
  - Unity tile size becomes layer scale.
  - Diffuse becomes albedo, with the diffuse remap baked in.
  - Unity's mask map holds metallic, occlusion, height and smoothness. It is remapped and split into an ORM map (occlusion, 1 − smoothness, metallic) and a height map.
  - Normal maps copy over as they are. A normal scale other than 1 and non-square tiling are reported, not converted.
- **Trees:**
  - Each prototype's prefab is matched to a project prefab of the same file name, with or without Unity's `prefab_` prefix. An ambiguous name is refused rather than guessed.
  - A matched prefab uses the project foliage type that already draws it, or a new one with default distances.
  - Unmatched trees are skipped and counted per prefab.
  - Name matching is a stopgap until converted assets record their Unity source.
- **Lights:**
  - Directional lights only.
  - Colour temperature is folded into the colour.
  - Intensity is multiplied by a request setting (default 2.5), since the two engines' units differ.
- **Camera:** the main camera becomes a camera entity, which drives the view in Play mode.

## Not built yet

- Placing the exported prefab instances.
- Converting models, materials and prefabs from Unity's own data. The exporter could export them as well, instead of the conversion guessing from `.mat` files.
- Terrain holes.
- Non-directional lights.
- More than one terrain is imported, but its tiles are not joined.
