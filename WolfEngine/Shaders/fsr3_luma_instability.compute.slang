// FSR3 upscaler - luma instability pass.
// Ported from ffx-fsr3/upstream/ffx_fsr3upscaler_luma_instability.h (SDK v1.1.2).

import "common_bindless.slang";
import "ffx_port_common.slang";
import "fsr3_constants.slang";
import "fsr3_common.slang";
import "fsr3_callbacks.slang";

struct LumaInstabilityFactorData
{
    FfxFloat32x4 fLumaHistory;
    FfxFloat32 fLumaInstabilityFactor;
};

LumaInstabilityFactorData ComputeLumaInstabilityFactor(
    LumaInstabilityFactorData data,
    FfxFloat32 fCurrentFrameLuma,
    FfxFloat32 fFarthestDepthInMeters)
{
    const FfxInt32 N_MINUS_1 = 0;
    const FfxInt32 N_MINUS_2 = 1;
    const FfxInt32 N_MINUS_3 = 2;
    const FfxInt32 N_MINUS_4 = 3;

    FfxFloat32 fLumaInstability = 0.0f;
    const FfxFloat32 fDiffs0 = fCurrentFrameLuma - data.fLumaHistory[N_MINUS_1];
    const FfxFloat32 fSimilarity0 = MinDividedByMax(
        fCurrentFrameLuma, data.fLumaHistory[N_MINUS_1], 1.0f);
    FfxFloat32 fMaxSimilarity = fSimilarity0;

    if (fSimilarity0 < 1.0f)
    {
        for (FfxInt32 i = N_MINUS_2; i <= N_MINUS_4; i++)
        {
            const FfxFloat32 fDiffs1 = fCurrentFrameLuma - data.fLumaHistory[i];
            const FfxFloat32 fSimilarity1 = MinDividedByMax(
                fCurrentFrameLuma, data.fLumaHistory[i]);
            if (sign(fDiffs0) == sign(fDiffs1))
            {
                fMaxSimilarity = ffxMax(fMaxSimilarity, fSimilarity1);
            }
        }
        fLumaInstability = FfxFloat32(fMaxSimilarity > fSimilarity0);
    }

    data.fLumaHistory[N_MINUS_4] = data.fLumaHistory[N_MINUS_3];
    data.fLumaHistory[N_MINUS_3] = data.fLumaHistory[N_MINUS_2];
    data.fLumaHistory[N_MINUS_2] = data.fLumaHistory[N_MINUS_1];
    data.fLumaHistory[N_MINUS_1] = fCurrentFrameLuma;
    data.fLumaHistory /= Exposure();
    data.fLumaInstabilityFactor = fLumaInstability *
        FfxFloat32(data.fLumaHistory[N_MINUS_4] != 0.0f);
    return data;
}

void LumaInstability(FfxInt32x2 pixel)
{
    if (IsOnScreen(pixel, RenderSize()) == false) return;

    LumaInstabilityFactorData data;
    data.fLumaInstabilityFactor = 0.0f;
    data.fLumaHistory = FfxFloat32x4(0.0f);

    const FfxFloat32x2 motion = LoadDilatedMotionVector(pixel);
    const FfxFloat32x2 uv = (pixel + 0.5f) / RenderSize();
    const FfxFloat32x2 currentJitteredUv = uv + Jitter() / RenderSize();
    const FfxFloat32x2 previousJitteredUv = uv + PreviousFrameJitter() / PreviousFrameRenderSize();
    const FfxFloat32x2 reprojectedUv = previousJitteredUv + motion;

    if (IsUvInside(reprojectedUv))
    {
        const FfxFloat32x2 reactiveUv = ClampUv(currentJitteredUv, RenderSize(), MaxRenderSize());
        const FfxFloat32x4 masks = SampleDilatedReactiveMasks(reactiveUv);
        const FfxFloat32 reactive = ffxSaturate(masks[REACTIVE]);
        const FfxFloat32 disocclusion = ffxSaturate(masks[DISOCCLUSION]);
        const FfxFloat32 shadingChange = ffxSaturate(masks[SHADING_CHANGE]);
        const FfxFloat32 accumulation = ffxSaturate(masks[ACCUMULAION]);

        if (accumulation > 0.9f)
        {
            const FfxFloat32x2 currentUv = ClampUv(currentJitteredUv, RenderSize(), MaxRenderSize());
            const FfxFloat32 currentLuma = SampleCurrentLuma(currentUv) * Exposure();
            const FfxFloat32x2 historyUv = ClampUv(
                reprojectedUv, PreviousFrameRenderSize(), MaxRenderSize());
            data.fLumaHistory = SampleLumaHistory(historyUv) * DeltaPreExposure() * Exposure();

            const FfxFloat32x2 farthestUv = ClampUv(
                currentJitteredUv, RenderSize() / 2, GetFarthestDepthMip1ResourceDimensions());
            const FfxFloat32 farthestDepth = SampleFarthestDepthMip1(farthestUv);
            data = ComputeLumaInstabilityFactor(data, currentLuma, farthestDepth);

            const FfxFloat32 velocityWeight = 1.0f - ffxSaturate(Get4KVelocity(motion) / 20.0f);
            data.fLumaInstabilityFactor *= velocityWeight * (1.0f - disocclusion) *
                (1.0f - reactive) * (1.0f - shadingChange);
        }
    }

    StoreLumaHistory(pixel, data.fLumaHistory);
    StoreLumaInstability(pixel, data.fLumaInstabilityFactor);
}

[numthreads(8, 8, 1)]
void Fsr3LumaInstabilityCS(int2 dispatchThreadId : SV_DispatchThreadID)
{
    LumaInstability(dispatchThreadId);
}
