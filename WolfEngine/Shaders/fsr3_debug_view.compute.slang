// FSR3 upscaler - debug view pass.
// Ported from ffx-fsr3/upstream/ffx_fsr3upscaler_debug_view.h (SDK v1.1.2).

import "common_bindless.slang";
import "ffx_port_common.slang";
import "fsr3_constants.slang";
import "fsr3_common.slang";
import "fsr3_callbacks.slang";

struct FfxDebugViewport
{
    FfxInt32x2 offset;
    FfxInt32x2 size;
};

FfxFloat32x2 GetDebugTransformedUv(FfxInt32x2 pixel, FfxDebugViewport viewport)
{
    return (FfxFloat32x2(pixel - viewport.offset) + 0.5f) / viewport.size;
}

FfxBoolean DebugPointInside(FfxInt32x2 pixel, FfxDebugViewport viewport)
{
    const FfxInt32x2 extent = viewport.offset + viewport.size;
    return pixel.x >= viewport.offset.x && pixel.x < extent.x &&
           pixel.y >= viewport.offset.y && pixel.y < extent.y;
}

void DrawDebugMotionVectors(FfxInt32x2 pixel, FfxDebugViewport viewport)
{
    const FfxFloat32x2 uv = ClampUv(GetDebugTransformedUv(pixel, viewport), RenderSize(), MaxRenderSize());
    const FfxFloat32x2 motion = SampleDilatedMotionVector(uv);
    StoreUpscaledOutput(pixel, FfxFloat32x3(0.5f + motion * RenderSize() * 0.5f, 0.5f));
}

void DrawDebugReactiveChannel(FfxInt32x2 pixel, FfxDebugViewport viewport, FfxInt32 channel)
{
    const FfxFloat32x2 uv = ClampUv(GetDebugTransformedUv(pixel, viewport), RenderSize(), MaxRenderSize());
    const FfxFloat32 value = ffxSaturate(SampleDilatedReactiveMasks(uv)[channel]);
    StoreUpscaledOutput(pixel, FfxFloat32x3(0.0f, value, 0.0f));
}

void DrawDebugProtectedAreas(FfxInt32x2 pixel, FfxDebugViewport viewport)
{
    const FfxFloat32 value = ffxSaturate(SampleHistory(GetDebugTransformedUv(pixel, viewport)).w - fLockThreshold);
    StoreUpscaledOutput(pixel, FfxFloat32x3(value, 0.0f, 0.0f));
}

void DrawDebugDepth(FfxInt32x2 pixel, FfxDebugViewport viewport)
{
    const FfxFloat32x2 uv = ClampUv(GetDebugTransformedUv(pixel, viewport), RenderSize(), MaxRenderSize());
    const FfxFloat32 depthMeters = GetViewSpaceDepthInMeters(SampleDilatedDepth(uv));
    StoreUpscaledOutput(pixel, FfxFloat32x3(ffxSaturate(depthMeters / 25.0f), 0.0f, 0.0f));
}

void DrawDebugLuma(FfxInt32x2 pixel, FfxDebugViewport viewport, FfxBoolean previousFrame)
{
    const FfxFloat32x2 uv = ClampUv(GetDebugTransformedUv(pixel, viewport), RenderSize(), MaxRenderSize());
    Texture2D<float4> lumaTexture = GetTexture(previousFrame ? previousLumaHandle : currentLumaReadHandle);
    SamplerState linearSampler = GetSampler(linearSamplerHandle);
    FfxFloat32 value = lumaTexture.SampleLevel(linearSampler, uv, 0.0f).x * Exposure();
    if (previousFrame) value *= DeltaPreExposure();
    StoreUpscaledOutput(pixel, FfxFloat32x3(ffxSaturate(value)));
}

void DebugView(FfxInt32x2 pixel)
{
    if (IsOnScreen(pixel, UpscaleSize()) == false) return;

    const FfxInt32x2 viewportSize = FfxInt32x2(UpscaleSize() * FfxFloat32x2(1.0f / 3.0f, 1.0f / 3.0f));
    FfxDebugViewport viewports[3][3];
    for (FfxInt32 y = 0; y < 3; y++)
    {
        for (FfxInt32 x = 0; x < 3; x++)
        {
            viewports[y][x].offset = viewportSize * FfxInt32x2(x, y);
            viewports[y][x].size = viewportSize;
        }
    }

    if (DebugPointInside(pixel, viewports[0][0])) DrawDebugMotionVectors(pixel, viewports[0][0]);
    if (DebugPointInside(pixel, viewports[0][1])) DrawDebugProtectedAreas(pixel, viewports[0][1]);
    if (DebugPointInside(pixel, viewports[0][2])) DrawDebugDepth(pixel, viewports[0][2]);
	if (DebugPointInside(pixel, viewports[1][0])) DrawDebugLuma(pixel, viewports[1][0], false);
	if (DebugPointInside(pixel, viewports[1][1])) DrawDebugLuma(pixel, viewports[1][1], true);
	if (DebugPointInside(pixel, viewports[1][2])) DrawDebugReactiveChannel(pixel, viewports[1][2], ACCUMULAION);
    if (DebugPointInside(pixel, viewports[2][0])) DrawDebugReactiveChannel(pixel, viewports[2][0], DISOCCLUSION);
    if (DebugPointInside(pixel, viewports[2][1])) DrawDebugReactiveChannel(pixel, viewports[2][1], SHADING_CHANGE);
    if (DebugPointInside(pixel, viewports[2][2])) DrawDebugReactiveChannel(pixel, viewports[2][2], REACTIVE);
}

[numthreads(8, 8, 1)]
void Fsr3DebugViewCS(int2 dispatchThreadId : SV_DispatchThreadID)
{
    DebugView(dispatchThreadId);
}
