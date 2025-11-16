# DX12 Render Graph Abstraction Plan

Goal: DX12 renderer becomes a backend-only concern (window/swapchain/devices). All passes (GBuffer, deferred lighting, future post) are authored + owned by the render graph, using `IGfx*` abstractions only.
No more pass-specific code in IRenderer implementations

## Step-by-step
1. **Isolate renderer surface ownership**
   - Keep `WolfRendererD3D` responsible only for window/swapchain creation and exposing device/backbuffer handles to the graph.
   - Replace `IRenderer.Execute*Pass` hooks with a single entry that hands the render graph a frame description (framebuffer size, swapchain/depth handles, pending draw commands).

2. **Upgrade render graph resource system**
   - Extend `RenderGraphResourceRegistry` to track per-pass usage (read/write + pass kind) and carry initial states for imported swapchain/depth.
   - Add compilation that materializes transient textures/buffers once per frame, computes transition barriers, and produces a pass execution schedule.

3. **Make passes API-agnostic units**
   - Move GBuffer/deferred lighting bodies out of `WolfRendererD3D` into `Rendering/Passes` using only `RenderGraphContext` + `IGfxCommandList`.
   - Pass configs should reference abstract pipelines, descriptor indices, and resource handles; no `ID3D12*` types.
   - Graphics passes own an explicit list of renderers/draw packets (from the scene submission layer) so command encoding happens inside the pass, not in `WolfRendererD3D`.

4. **Pipeline/shader resolution via graph**
   - Introduce `PipelineKey` + shader set inputs on each pass; during graph compile resolve to `IGfxPipeline` via `IGfxDevice.GetOrCreatePipeline`.
   - Centralize root-signature/PSO creation in `D3D12Device` instead of ad-hoc inside passes.
   - Use Slang reflection on material shaders to tag which passes they participate in (e.g., GBuffer vs. forward/lighting), so the graph can route materials to the right passes without renderer-specific wiring.

5. **Descriptor/bindless plumbing**
   - Implement `IGfxDescriptorTable` for DX12 (CBV/SRV/UAV heap) and surface allocation helpers so passes request SRV/UAV slots without touching descriptor heaps directly.
   - Feed descriptor indices through `RenderGraphContext` so compute/graphics passes bind resources uniformly.

6. **Command recording owned by graph**
   - Have `RenderGraph.Execute` start the appropriate command list per pass (`BeginGraphics`/`BeginCompute`), issue compiled barriers, run the pass callback, then close/submit—removing `_activeCommandList` usage from `WolfRendererD3D`.
   - Ensure present-time transitions (RTV → Present) are also emitted by the graph using the imported backbuffer handle.

7. **Scene data ingestion through graph**
   - Convert `_pendingCommands`/`_drawCommands` into per-frame buffers managed by the graph (e.g., upload arena + draw packets).
   - GBuffer pass consumes those packets through the abstract command list (vertex/index buffers already wrapped by `IGfxBuffer`).

8. **Validation + cleanup**
   - Add lightweight validation (missing executes, incompatible formats, unbound pipelines) during graph compile.
   - Remove remaining DX12-specific fields from passes/renderer after the above (descriptor heaps, root signatures, PSOs stored only in backend caches).
