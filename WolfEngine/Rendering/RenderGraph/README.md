# Render Graph Roadmap

This document captures how the render graph scaffolding evolves into the v1 rendering featureset outlined in `Plan.md`. It is meant to guide implementation order and clarify responsibilities as we grow from today's stubbed classes to a feature-complete frame graph.

## 1. Goals
- Deterministic pass scheduling: allow authoring passes declaratively so dependency ordering and resource lifetime are handled centrally.
- Transient resource pooling: create render targets, depth buffers, and UAVs on demand while reusing allocations across frames.
- Cross-backend viability: keep abstractions neutral enough to back both Direct3D12 and Metal.
- Debuggability: provide naming, scopes, and validation hooks so we can inspect frame structure at runtime.

## 2. Current State (R0)
- `RenderGraph`, `RenderGraphBuilder`, `RenderGraphPass`, and `RenderGraphContext` exist but only store callbacks and execute them sequentially.
- `RenderGraphResourceRegistry` tracks transient texture descriptors but does not create real GPU resources.
- Resource handles are simple integers with no lifetime or barrier tracking.

These pieces are sufficient to record a single pass that runs arbitrary code, which is the next integration milestone described below.

## 3. Immediate Integration (R1)
1. Instantiate the render graph inside `WolfRendererD3D`.
2. During `OnRender`, build a pass that clears the swapchain render target.
3. Finish by calling `RenderGraph.Execute()` each frame.

**Outcome:** Verifies pass authoring flow and frames the work required to bind real GPU surfaces to resource handles. This gets the graph visible in the renderer loop before adding complexity.

**Direct3D12 status:** Implemented. `WolfRendererD3D` now records a “Clear Backbuffer” pass each frame and executes the graph before issuing scene draws.

## 4. Resource Realisation (R2)
- Extend `RenderGraphResourceRegistry` to produce backend-specific texture objects (RTVs/DSVs for D3D12, texture heaps for Metal).
- Track load/store ops and attachment descriptions per pass.
- Inject barrier planning: when a pass declares a write followed by a read, emit the necessary state transitions.

**Feature tie-in:** Enables the deferred base pass + clustered lighting because G-Buffer attachments and depth hierarchy can be allocated transiently and shared across passes.

## 5. Pass Graph + Scheduling (R3)
- Allow pass dependencies via explicit resource reads/writes; topologically sort passes before execution.
- Add culling of unused passes and resources (e.g., debug-only overlays not requested this frame).
- Introduce async compute lanes in the schedule once dependencies allow it.

**Feature tie-in:** Required for volumetrics on async compute, post-processing stacks following the base pass, and future DDGI probes that can run in parallel.

## 6. Frame Lifetime Services (R4)
- Provide per-frame scratch arenas for CPU/GPU data uploads.
- Collect statistics (pass duration, resource footprints) for ImGui debug tools.
- Hook into the engine’s ECS so camera, light, and material buffers stream in through graph-managed upload buffers.

**Feature tie-in:** Supports persistent mapped uploads and frame arenas listed in `Plan.md`, and drives tooling such as the ImGui frame debugger.

## 7. Advanced Usage (R5)
- Implement history resources with ping-pong logic (needed for TAA, volumetrics, exposure).
- Integrate multi-resolution workflows (half-res froxel grids, quarter-res post effects).
- Support external resource import/export so platform swapchains and editor views can be composed with the graph.

**Feature tie-in:** Unlocks the full post-processing suite, temporal accumulation techniques, and editor overlays demanded by the minimal engine featureset.

## 8. Validation and Tooling
- Add optional validation layer that checks for missing resource states, incompatible formats, or forgotten execute callbacks.
- Emit frame captures (JSON / ImGui viewers) summarizing the compiled graph for debugging.
- Integrate with the planned ImGui world panel to display pass graphs and allow ad-hoc toggles.

## 9. Summary
Advancing through R1–R5 transforms the placeholder render graph into the backbone required for the minimal engine goals: deferred lighting, volumetrics, post-processing, and tooling support. Each stage builds on the current classes without breaking existing renderer behaviour, ensuring we converge on the planned featureset with incremental, verifiable steps.
