# Render features

A render feature is game code that adds its own GPU work to the engine's frame: a compute
kernel that reads the scene and changes it, such as fog of war darkening the ground outside
the player's sight. The engine runs it at a fixed point in every view, without knowing what
it is for.

A feature has two halves on two threads. `Extract` runs on the game thread and copies what
the GPU work needs into a frame object. `Record` runs on the render thread and adds passes
using only that frame object. The engine hands frame objects across the threads.

## Compute shaders

Kernels are written in Slang, in a `.compute.slang` file anywhere under the project's
`Assets` folder. The editor imports the file as a `ComputeShader` asset, which an
`AssetRef<ComputeShader>` field can reference like any other asset. Each function marked
`[shader("compute")]` is one kernel:

```slang
import wolf;

cbuffer FogSettings : register(b0)
{
    uint sceneColorHandle;
    uint depthHandle;
    uint maskHandle;
    uint maskSamplerHandle;
    float2 gridOrigin;
    float2 gridSize;
    float darkness;
};

[shader("compute")]
[numthreads(8, 8, 1)]
void DarkenCS(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= WolfRenderSize())) return;
    float depth = GetTexture(depthHandle).Load(int3(id.xy, 0)).r;
    if (WolfIsBackgroundDepth(depth)) return;

    float3 position = WolfReconstructWorldPosition(id.xy, depth);
    float2 uv = (position.xz - gridOrigin) / gridSize;
    float visible = GetTexture(maskHandle).SampleLevel(GetSampler(maskSamplerHandle), uv, 0).r;

    RWTexture2D<float4> color = GetRWTexture(sceneColorHandle);
    color[id.xy] *= lerp(darkness, 1.0f, visible);
}
```

`import wolf;` is the engine's public shader module. It provides:

- `GetTexture`, `GetRWTexture` and `GetSampler`, which turn a handle into a texture or sampler.
- `WolfRenderSize()`, the size of the scene colour and depth textures.
- `WolfReconstructWorldPosition(pixel, depth)` and `WolfIsBackgroundDepth(depth)`.

Other engine shader files are internal and may change. Do not import them.

Declare your own constants in cbuffers at `b0`, `b1`, `b2` or `b11`. The engine fills
`WolfView` at `b12`, and import fails with a message for any other register.

Kernels are compiled at import, for the platform the editor runs on: D3D12 on Windows, Metal
on macOS. A build for a platform needs the shader imported on that platform, like textures.
Other `.slang` files are modules that compute shaders can import, not assets. Editing one
does not reimport the shaders that use it, so reimport them by hand.

## Writing a feature

Derive from `RenderFeature<TFrame>`, where `TFrame` is a class holding what one frame needs:

```csharp
using WolfEngine.ECS;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Features;

public sealed class FogFrame
{
    public ComputeShader? Shader;
    public long Revision;
    public byte[] Mask = new byte[256 * 256 * 2];
    public Vector2 GridOrigin;
    public Vector2 GridSize;
}

public sealed class FogDarkeningFeature(FogOfWar fog, AssetRef<ComputeShader> shader) : RenderFeature<FogFrame>
{
    private static readonly SamplerDescriptor LinearClamp =
        new(FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp);

    private RenderFeatureTexture? _mask;
    private long _uploadedRevision = -1;

    public override RenderInjectionPoint InjectionPoint => RenderInjectionPoint.BeforeTemporalResolve;

    // Render thread, once.
    protected override void Initialize(RenderFeatureResources resources) =>
        _mask = resources.CreateTexture("Fog mask", 256, 256, TextureFormat.R16Unorm);

    // Game thread, every published frame.
    protected override void Extract(World world, FogFrame frame)
    {
        frame.Shader = shader.Asset;
        frame.Revision = fog.Revision;
        fog.CopyVisibleMask(frame.Mask);
        frame.GridOrigin = fog.Origin;
        frame.GridSize = fog.Size;
    }

    // Render thread, every view.
    protected override void Record(RenderFeatureContext context, FogFrame frame)
    {
        if (frame.Shader is null) return;
        if (frame.Revision != _uploadedRevision)
        {
            context.Upload(_mask!, frame.Mask);
            _uploadedRevision = frame.Revision;
        }

        context.AddComputePass("Darken", frame.Shader, "DarkenCS")
            .ReadWrite("sceneColorHandle", context.SceneColor)
            .Read("depthHandle", context.Depth)
            .Read("maskHandle", _mask!)
            .Set("maskSamplerHandle", LinearClamp)
            .Set("gridOrigin", frame.GridOrigin)
            .Set("gridSize", frame.GridSize)
            .Set("darkness", 0.45f)
            .Dispatch(context.RenderSize);
    }
}
```

The rules that keep the two threads apart:

- `Extract` must overwrite every field of the frame object. Frame objects are pooled and
  reused, so a field it skips still holds an older frame's value.
- `Record` reads the frame object and the feature's render-thread state, such as textures
  made in `Initialize`, and nothing else. Do not touch the world or gameplay objects there.
- Resolve assets in `Extract`, as `frame.Shader = shader.Asset` does. A reimported shader is
  then picked up on the next frame.

In a pass, `Read` and `ReadWrite` both declare a texture to the render graph and write its
handle into the named `uint` constant. `Set` writes other constants by name. A field inside
a struct is named `member.field`. `Dispatch` takes thread counts, not thread groups, and the
kernel must bounds-check. Every pass that is added must be dispatched.

`Upload` replaces a texture's contents from tightly packed rows of texels. It copies the data
straight away, and the GPU does the copy in frame order, so frames already in flight keep the
old contents. A texture keeps its contents across frames, so upload only when the data
changes. Only textures the feature created can be uploaded to.

## Where features run

`BeforeTemporalResolve` runs after opaque lighting and transparent objects, before TAA or
FSR3. Scene colour is linear HDR at render resolution, so the temporal resolve smooths the
feature's output along with the rest of the frame. A feature runs in every view that renders
its world, which includes the editor's scene view during play. `context.View` tells views
apart.

## Registering

Get `IRenderFeatureRegistry` from the service provider and register a feature for a world,
usually in `IGameplayModule.OnLoaded`:

```csharp
public void OnLoaded(World world) => _renderFeatures.Register(world, _fogDarkening);
```

`Unregister` stops a feature and releases the textures it created once no frame on the GPU
uses them. When gameplay unloads, the engine unregisters every feature of the world after
`OnUnloading`. A feature that was not unregistered by then cannot keep running against a
world that is gone, or keep a reloaded gameplay assembly loaded. An unregistered feature
cannot be registered again; create a new instance.

## Failures

A feature runs game code on the render thread. If `Initialize`, `Extract` or `Record` throws,
or a pass is added but never dispatched, the engine disables that feature, reports the
exception once in the log, and keeps rendering the frame without it. Fix the code and reload
to run it again.

## Limits

- One injection point, and compute passes only. More will be added when a feature needs them.
- Textures a feature creates are 2D, uncompressed and single-mip. Buffers are not offered yet.
- `Upload` on Metal is untested so far: it compiles, but has not run on a Mac.
