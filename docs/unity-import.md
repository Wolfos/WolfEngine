# Unity scene import

Status:
- **Imports:** a terrain with its layers and trees, the prefab instances a scene places, directional lights and the main camera.
- **Converted from Unity's own data:** the prefabs, their meshes and their materials.
- **On the Forest demo:** 5,365 trees and 5,915 of 6,091 prop placements import. The rest are particle systems, wind zones and prefabs that draw nothing in Unity either.

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

Import with the `import_unity_scene` automation tool. It takes:
- the export folder;
- the Unity project, which textures are read from;
- a target folder under `Assets`;
- a new scene name.

It returns what was imported, skipped and approximated. Assets go under the target folder in `Terrain`, `Meshes`, `Materials`, `Textures`, `Prefabs` and `Foliage Types`. On the Forest demo the import takes about five minutes, most of it texture conversion.

## The export folder

`scene.json` holds format version 1:

- **Terrains:** position, size, map resolutions, layers (textures, tiling, remaps), tree prototypes (prefab paths), and trees. Tree positions are 0 to 1 across the terrain; each tree has a rotation about Y and separate width and height scales.
- **Prefab instances:** the outermost prefab instance roots, with their prefab path and world transform. Each one also records how many overrides it has beyond its root transform, and of what kind.
- **Prefabs:** every prefab the scene places, as terrain trees or as instances.
  - Each prefab's hierarchy with local transforms, and its mesh renderers with their mesh and materials.
  - Its LOD group, with its reference point, size and per-level renderers.
  - The other components on each node, named so the import can report them.
- **Materials:** every material those prefabs use, with each property its shader declares (floats, colours, vectors, textures with tiling), its shader name and keywords.
- **Meshes:** every mesh, as glTF under `meshes/`, with one glTF mesh per submesh named `sub0`, `sub1` and so on.
- **Loose mesh objects, lights and cameras,** with world transforms.

Beside it, per terrain:

- `terrain_N_heights.r16`: 16-bit heights, row-major, rows along +Z.
- `terrain_N_alphamaps.u8`: one byte plane per layer, in the same row order.

Detail prototypes are not exported. WolfEngine generates detail from terrain layers (see `foliage.md`).

## Conversion

- **Space** (`UnitySpace`):
  - Both engines are left-handed, +Y up, +Z forward, in metres, so positions, rotations and scales carry over unchanged.
  - That holds for models only because they come from the exporter's glTF rather than the source FBX files. The two engines mirror an FBX along different axes, which would leave a model turned a half turn about Y.
  - The exporter negates Z for glTF, which is right-handed, and reverses each triangle. Mirroring moves the viewer with the geometry, so Unity's clockwise front faces would otherwise become glTF back faces. WolfEngine's importer negates Z again and gets Unity's vertices back exactly.
- **Terrain:**
  - A Unity terrain spans from its position along +X and +Z. A WolfEngine terrain is centred on its entity, so the entity goes at the Unity terrain's centre.
  - The heightmap carries over sample for sample, with the height scale set to the Unity height range.
  - The nearest terrain level is drawn at the heightmap's spacing, 128 quads per 64 m chunk on the Forest demo, and ray tracing at half that. The component default of 32 quads smooths away most of a Unity heightmap's detail.
  - Each layer map texel keeps its four heaviest Unity layers, the most a WolfEngine texel holds. The import reports the mean weight dropped; on the Forest demo it is zero.
- **Layers:**
  - Unity tile size becomes layer scale.
  - Diffuse becomes albedo, with the diffuse remap baked in.
  - Unity's mask map holds metallic, occlusion, height and smoothness. It is remapped and split into an ORM map (occlusion, 1 − smoothness, metallic) and a height map.
  - Normal maps copy over as they are. A normal scale other than 1 and non-square tiling are reported, not converted.
- **Prefabs** (`UnityPrefabImporter`):
  - Each Unity node becomes an entity with its local transform. A renderer with several submeshes becomes one child entity per submesh, since a WolfEngine renderer draws one mesh with one material.
  - A LOD group becomes a `LodGroup`. Unity's transition heights carry over directly, since both engines measure the bounds' projected height on screen. The radius is half Unity's group size, and the last transition, where Unity culls, is dropped. Renderers get the level range of the LODs that list them.
  - Inactive nodes and disabled renderers are left out. Other components are listed in the report.
- **Materials** (`UnityMaterialConversion`):
  - Every shader the pack uses follows HDRP Lit's property names, sometimes with a layer prefix (`_TrunkBaseColorMap`, `_BaseNormalMap`) or suffix (`_BaseColorMap0`). The layer is found from the textures present, and the first one is converted.
  - Colours are converted from sRGB to linear.
  - The HDRP mask map (metallic, occlusion, detail mask, smoothness) is remapped and repacked as ORM.
  - The alpha-test and double-sided keywords carry over.
  - **Reported approximations:** other layers (bark, top cover, layered-lit layers), the per-instance dry colour, and texture tiling, which WolfEngine materials do not have.
- **Trees:** each prototype uses its converted prefab, through the project foliage type that already draws it or a new one with default distances. Width and height scale carry over separately.
- **Prefab instances:**
  - Each one is placed as a linked prefab instance with its world transform, under a `Props` entity.
  - Prefabs that draw nothing are skipped and counted per reason.
  - Overrides other than static flags are counted per kind, since a placement keeps only its transform.
- **Lights:**
  - Directional lights only.
  - Colour temperature is folded into the colour.
  - Intensity is multiplied by a request setting (default 2.5), since the two engines' units differ.
- **Camera:** the main camera becomes a camera entity, which drives the view in Play mode.

## Not built yet

- Particle systems, wind zones and other non-mesh components.
- Material layers beyond the first, the dry colour variation, and texture tiling.
- Instance overrides other than transforms.
- Re-importing into an existing scene. Each import writes a new scene and new assets.
- Terrain holes.
- Non-directional lights.
- More than one terrain is imported, but its tiles are not joined.
