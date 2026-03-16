# Minimal engine feature set (v1)

**Rendering**

- Render graph
- Deferred base pass (small G-buffer) + clustered/forward+ for transparents.
- Atmosphere/sky model, sun/moon, cascaded shadows.
- Volumetrics: half-res froxel grid with history; async compute if possible.
- Post: TAA (stable!), exposure, bloom, color grading.
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


## Code review
Findings

[High] Missing UAV barriers between passes that write the same UAV: RenderGraphCompiler only inserts barriers on state changes, so consecutive UAV writes/read‑after‑write in D3D12 won’t get a UAV barrier and can produce stale/undefined results. RenderGraphCompiler.cs (line 41)
[Medium] Metal texture pooling lacks GPU completion gating: RenderGraphResourceRegistry.BeginFrame returns transient textures to the pool, MetalDevice immediately reuses them, and WolfRendererMetal.BeginFrame does not wait on command buffer completion, so textures can be recycled while still in flight. RenderGraphResourceRegistry.cs (line 80), MetalDevice.cs (line 232), WolfRendererMetal.cs (line 593)
[Medium] Render‑graph handles are effectively frame‑scoped but IDs reset every BeginFrame; any handle cached across frames can alias a different resource and bind the wrong texture. RenderGraphResourceRegistry.cs (line 124), RenderGraphResourceHandle.cs (line 9)
[Low] Intra‑pass read‑then‑write transitions aren’t expressible: barriers are injected only before the pass, so a pass that reads and later writes the same resource can’t get a mid‑pass transition without manual barriers. RenderGraphCompiler.cs (line 41), RenderGraphPass.cs (line 60)
Open Questions / Assumptions

Are render‑graph handles intended to be strictly per‑frame? If so, can we document/guard it (e.g., generation IDs)?
Do you want UAV barriers to be auto‑inserted (D3D12) or expressed explicitly per pass?