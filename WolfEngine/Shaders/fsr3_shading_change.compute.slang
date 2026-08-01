// FSR3 upscaler - shading change pass.
//
// Ported from ffx-fsr3/upstream/ffx_fsr3upscaler_shading_change.h (FidelityFX SDK v1.1.2).
//
// Collapses the shading-change pyramid into a single half-render-resolution mask. Each of
// the first three levels contributes |difference * signSum|: multiplying the magnitude by
// the summed sign means a change only survives if the pixels underneath agreed on its
// direction, which is what separates a real lighting change from temporal noise.
//
// Taking the maximum across levels rather than the average keeps a change that is sharp at
// one scale from being diluted by the scales where it is not visible.
//
// Runs at ShadingChangeRenderSize (half render resolution), which is where the pyramid's
// level 0 already sits.

import "common_bindless.slang";
import "ffx_port_common.slang";
import "fsr3_constants.slang";
import "fsr3_common.slang";
import "fsr3_callbacks.slang";

static const FfxInt32 s_MipLevelsToUse = 3;

struct ShadingChangeLumaInfo
{
    FfxFloat32 fSamples[s_MipLevelsToUse];
};

ShadingChangeLumaInfo ComputeShadingChangeLuma(
    FfxInt32x2 iPxPos,
    FfxFloat32x2 fUv,
    const FfxInt32x2 iCurrentSize)
{
    ShadingChangeLumaInfo info;

    const FfxFloat32x2 fMipUv = ClampUv(fUv, ShadingChangeRenderSize(), GetSPDMipDimensions(0));

    [unroll]
    for (FfxInt32 iMipLevel = iShadingChangeMipStart; iMipLevel < s_MipLevelsToUse; iMipLevel++)
    {
        const FfxFloat32x2 fSample = SampleSPDMipLevel(fMipUv, FfxUInt32(iMipLevel));

        // .x is the signed relative difference, .y the summed sign. Their product is large
        // only where a change is both strong and consistent across the level's footprint.
        info.fSamples[iMipLevel] = abs(fSample.x * fSample.y);
    }

    return info;
}

void ShadingChange(FfxInt32x2 iPxPos)
{
    if (IsOnScreen(FfxInt32x2(iPxPos), ShadingChangeRenderSize()))
    {
        const FfxFloat32x2 fUv = (iPxPos + 0.5f) / ShadingChangeRenderSize();
        const FfxFloat32x2 fUvJittered = fUv + Jitter() / RenderSize();

        const ShadingChangeLumaInfo info = ComputeShadingChangeLuma(
            iPxPos,
            fUvJittered,
            ShadingChangeRenderSize());

        const FfxFloat32 fScale = 1.0f + iShadingChangeMipStart / s_MipLevelsToUse;
        FfxFloat32 fShadingChange = 0.0f;
        [unroll]
        for (int iMipLevel = iShadingChangeMipStart; iMipLevel < s_MipLevelsToUse; iMipLevel++)
        {
            if (info.fSamples[iMipLevel] > 0)
            {
                fShadingChange = ffxMax(fShadingChange, info.fSamples[iMipLevel]) * fScale;
            }
        }

        StoreShadingChange(iPxPos, ffxSaturate(fShadingChange));
    }
}

// Dispatch is ceil(ShadingChangeRenderSize / 8) - see Fsr3ShadingChangePass.Record.
[numthreads(8, 8, 1)]
void Fsr3ShadingChangeCS(int2 dispatchThreadId : SV_DispatchThreadID)
{
    ShadingChange(dispatchThreadId);
}
