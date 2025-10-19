# Minimal engine feature set (v1)

**Rendering**

- Render graph
- Deferred base pass (small G-buffer) + clustered/forward+ for transparents.
- Atmosphere/sky model, sun/moon, cascaded shadows.
- Volumetrics: half-res froxel grid with history; async compute if possible.
- Post: TAA (stable!), exposure, bloom, color grading, depth of field.
- Optional tier: DDGI.

**Engine**

- ECS + GameObject system
- Input
- Physics (Jolt)
- Skeletal animations

**World**

- Camera-relative floats + origin rebasing on a coarse grid.
- Streaming lite: heightfield terrain + foliage + rocks via cell grid; single biome to start.
- GPU culling + multi-draw indirect; impostors for far distances.

**Tools**

- ImGui-based world panel (paint terrain/foliage, place props/spawners), TOD scrubber, weather sliders.
- Offline asset cookers

**Infra (C#)**

- Silk.NET bindings; blittable interop only; persistent mapped uploads; frame arenas; Server GC; consider NativeAOT for shipping.