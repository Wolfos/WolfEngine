3# Target Renderer Structure (Summary for Codex)

## High-level Layers (one-way deps)
1) **Render Pipeline (game-facing)**  
   - Builds the frame by adding passes to the graph (GBuffer, Lighting, Volumetrics, Post, Present).  
   - Talks **only** to the RenderGraph API. No API objects.

2) **RenderGraph (engine core, API-agnostic)**  
   - Owns **virtual resources** (textures/buffers/views), pass DAG, lifetimes, aliasing, and **state transitions**.  
   - During **Compile**: resolves virtual → physical resources, builds barrier plans, and resolves pipelines from API-agnostic **PipelineKeys**.  
   - During **Execute**: records pass bodies to **IGfxCommandList** (graphics or compute) provided by the backend, then submits.

3) **Backend (API implementations: DX12 / Metal)**  
   - Creates **physical resources**, **pipelines/PSOs**, manages **descriptors/argument buffers**, and executes command lists.  
   - Implements barriers, queues, swapchain, fences.  
   - Knows nothing about pass semantics—just generic draw/dispatch.

---

## Minimal Project Layout
```
Engine.Rendering/
  Abstraction/          # API-agnostic interfaces + small structs
  RenderGraph/          # Graph, resources, compiler, executor
  Backend.DX12/         # Silk.NET D3D12 implementation
  Backend.Metal/        # SharpMetal implementation
  Passes/               # API-agnostic pass code (GBuffer, Lighting, etc.)
  Shaders/              # .slang sources; Slang emits DXIL/MSL
  Renderer.cs           # ties device + graph + pipeline together
```
---

## Core API-agnostic Interfaces (tiny)
```csharp
public enum PassKind { Graphics, Compute }
public interface IGfxDevice {
  IGfxCommandList BeginGraphics();
  IGfxCommandList BeginCompute();
  void Submit(IGfxCommandList cl);
  ITexture CreateTexture(TextureDesc d);
  IBuffer  CreateBuffer(BufferDesc d);
  IPipeline GetOrCreatePipeline(PipelineKey key, ShaderBlobSet blobs);
  IDescriptorTable GlobalTable { get; } // bindless (heap/arg buffer)
}

public interface IGfxCommandList {
  void BeginPass(PassTargets rt, Viewport vp);   // graphics
  void EndPass();                                 // graphics
  void BindPipeline(IPipeline p);
  void SetBindlessTable(IDescriptorTable table);
  void PushConstants<T>(in T data) where T: unmanaged;
  void SetVertexBuffers(ReadOnlySpan<VertexBufferView> vbs);
  void SetIndexBuffer(in IndexBufferView ib);
  void Draw(in DrawArgs a);                       // graphics
  void Dispatch(uint x, uint y, uint z);         // compute
  void Barrier(in Barrier b);                     // graph-generated
}
```
---

## RenderGraph API (authoring → compile → execute)
```csharp
public sealed class RenderGraph {
  RGTexture CreateTexture(TextureDesc d, string name);
  RGBuffer  CreateBuffer(BufferDesc d, string name);

  void AddPass(string name, PassKind kind,
    Action<RGPassBuilder> setup,
    Action<RGPassContext> exec);

  void Compile(IGfxDevice dev, ShaderLibrary shaders); // alloc, alias, pipelines, barriers
  void Execute(IGfxDevice dev, FrameState frame);      // record + submit
}

public sealed class RGPassBuilder {
  void Read (RGTexture t, Stage stage = Stage.Pixel);
  void Write(RGTexture t, Stage stage = Stage.Pixel);
  void ReadWrite(RGTexture t, Stage stage = Stage.Compute); // UAVs etc.
  // (Same for RGBuffer)
}

public readonly struct RGPassContext {
  public IGfxCommandList Cmd { get; }
  public IDescriptorTable Bindless { get; }
  public Dictionary<string, IPipeline> Pipelines { get; } // resolved at Compile
  public Viewport Viewport { get; }
  public PassTargets Targets { get; } // concrete RTV/DSV when graphics
  public PerFrameConstants PerFrame { get; }
}
```
---

## Pipelines & Shaders
- **PipelineKey** (API-agnostic): `{ PassKind, VS/PS/CS entry names, RenderState, RT formats, DSV format }`.  
- **SlangCompiler** emits **DXIL** for DX12 and **MSL** for Metal from the same `.slang` sources + provides minimal reflection.  
- Graph **Compile** calls `GetOrCreatePipeline(PipelineKey, Blobs)` on the backend; passes just refer to pipelines by key/ID.

---

## Resources & Bindless
- Passes use **virtual** `RGTexture/RGBuffer`. Graph maps them to backend `ITexture/IBuffer`.  
- A single **bindless** `IDescriptorTable` (global) exposes `AllocateSRV/UAV/CBV/Sampler` and returns **indices** used in shaders.  
- DX12: table → CBV/SRV/UAV descriptor heap; Metal: table → argument buffer.

---

## Barriers & Queues
- Pass builder declares **usage** (read/write + stage).  
- Graph compiles edges to backend **barriers** (DX12 transitions/UAV; Metal typically encoder boundaries + `useResource`).  
- `PassKind` selects queue (Graphics/Compute). If you add async compute later, the graph inserts **Signal/Wait** fences automatically when dependencies cross queues.

---

## Example Pass (API-agnostic)
```csharp
rg.AddPass("DeferredLighting", PassKind.Compute,
  setup: b => {
    b.Read(gbuffer0, Stage.Compute);
    b.Read(gbuffer1, Stage.Compute);
    b.Read(depth,   Stage.Compute);
    b.ReadWrite(hdrTarget, Stage.Compute); // UAV
  },
  exec: ctx => {
    ctx.Cmd.BindPipeline(ctx.Pipelines["DeferredCS"]);
    ctx.Cmd.SetBindlessTable(ctx.Bindless);
    ctx.Cmd.PushConstants(ctx.PerFrame);
    uint gx = (uint)Math.Ceiling(ctx.Viewport.Width / 8.0f);
    uint gy = (uint)Math.Ceiling(ctx.Viewport.Height / 8.0f);
    ctx.Cmd.Dispatch(gx, gy, 1);
  });
```
---

## Data Flow at Runtime
1) **Pipeline** builds the frame via `RenderGraph.AddPass(...)`.  
2) **Graph.Compile**:  
   - Resolve/allocate textures & buffers.  
   - Compute barriers and aliasing.  
   - Resolve **PipelineKeys → IPipeline** via backend and Slang.  
3) **Graph.Execute**:  
   - For each pass: create appropriate **IGfxCommandList** (graphics/compute), apply barriers, run `exec(ctx)`, submit.  
4) **Backend** turns commands into DX12 or Metal calls, manages heaps/encoders/fences.

---

## Incremental Refactor Steps (DX12-tied → agnostic)
1) Introduce `Abstraction/` interfaces (above) without changing behavior.  
2) Wrap current DX12 objects behind those interfaces (first: CommandList, Texture, Pipeline).  
3) Move DX12 specifics (descriptor heaps, RTV/DSV, barriers, PSO) into **Backend.DX12**.  
4) Update passes to use only `RGPassContext.Cmd` + resolved resources; remove DX12 types from pass code.  
5) Add **Slang DXIL/MSL** path + `PipelineKey` resolution in graph **Compile**.  
6) Implement **Backend.Metal** with the same interfaces (argument buffers for bindless).  
7) (Later) Add async compute + cross-queue fences in the graph.

This is the lean, modern, cross-API structure you can hand to Codex.
