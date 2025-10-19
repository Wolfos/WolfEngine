# Renderer Architecture Refactor Notes

## Current Situation
`WolfRendererD3D` currently does double duty:
- It still contains the “legacy” forward rendering path.
- It now records and executes the new render graph, hand-authoring the G-buffer and lighting passes.

If we keep growing this file, the Metal renderer will have to duplicate every pass definition once we port it, even before we layer in platform specifics.

## Proposed Split
### Shared Rendering Layer
- Owns the `RenderGraph`, camera state, draw lists, and per-frame orchestration.
- Declares passes (G-buffer, lighting, post-processing) in renderer-agnostic terms.
- Talks only in terms of abstract resource handles and graph callbacks.

### Backend Adapters
- D3D12 and Metal implement a common backend interface:
  - Texture allocation/import (swapchain, depth, transient RTV/DSV/MSAA surfaces).
  - Encoding callbacks that turn the shared pass description into native command buffer work.
- Each backend is responsible for device/swapchain creation and submitting the final command buffer.

### Material & Pipeline Setup
- Move shader compilation and pipeline creation into shared factories keyed by “shading model”.
- Backend code converts the compiled shader blobs into native PSOs (D3D12) or render pipeline states (Metal).
- Shared layer maps engine materials to passes once; backends just bind the translated pipeline state.

### Frame Orchestration
- Shared layer drives `BeginFrame`, pass registration, and `Execute`.
- Backend only imports the swapchain/depth textures and provides native command encoders/lists to the callbacks.

## Next Steps
1. Extract the G-buffer/forward pass setup into a new shared class and have both renderers use it.
2. Extend the backend interface so Metal can plug in while reusing the pass definitions.
3. Port `WolfRendererMetal` onto the render graph to validate the shared layer.

This keeps per-API code minimal and ensures new passes or effects are authored once and run everywhere.
