// FSR3 upscaler - RCAS (Robust Contrast Adaptive Sharpening) pass.
//
// Ported from FidelityFX SDK v1.1.2:
//   ffx-fsr3/upstream/ffx_fsr3upscaler_rcas.h  (the dispatch wrapper)
//   ffx-fsr3/upstream/ffx_fsr1.h               (FsrRcasF, the actual filter kernel)
//
// Note that RCAS's kernel lives in the FSR1 header, not the FSR3 one - the FSR3 file
// is only glue that supplies the load callback and applies exposure around it.
//
// This is the fp32 path. Upstream's packed-fp16 and Xbox paired variants are omitted;
// the port targets numerical parity with the reference first.
//
// In the finished effect this consumes the accumulate pass's output at display
// resolution. Until accumulate is ported it runs on whatever the render graph binds
// as RCAS input, which is what makes it usable as the first end-to-end slice.

import "common_bindless.slang";
import "ffx_port_common.slang";
import "fsr3_constants.slang";
import "fsr3_callbacks.slang";

cbuffer cbRCAS : register(b2)
{
    uint rcasConfigX;
    uint rcasConfigY;
    uint rcasConfigZ;
    uint rcasConfigW;
};

FfxUInt32x4 RCASConfig()
{
    return uint4(rcasConfigX, rcasConfigY, rcasConfigZ, rcasConfigW);
}

// Upstream enables the denoise term for FSR3's RCAS (FSR_RCAS_DENOISE 1 in
// ffx_fsr3upscaler_rcas.h). Without it, sharpening amplifies temporal noise that the
// accumulator has not yet resolved.
static const float FSR_RCAS_LIMIT = 0.25f - (1.0f / 16.0f);

// ---------------------------------------------------------------------------
// Load callback. Upstream multiplies into exposed space before filtering and divides
// back out afterwards, so that the contrast measurements are made on values the
// tonemapper will actually see.
// ---------------------------------------------------------------------------

FfxFloat32x4 FsrRcasLoadF(FfxInt32x2 pixel)
{
    FfxFloat32x4 colour = LoadRCAS_Input(pixel);
    colour.rgb *= Exposure();
    return colour;
}

// ---------------------------------------------------------------------------
// FsrRcasF, ported verbatim from ffx_fsr1.h.
//
// Uses a minimal 3x3 neighbourhood:
//        b
//      d e f
//        h
// ---------------------------------------------------------------------------

void FsrRcasF(
    out FfxFloat32 pixR,
    out FfxFloat32 pixG,
    out FfxFloat32 pixB,
    FfxUInt32x2 ip,
    FfxUInt32x4 con)
{
    FfxInt32x2 sp = FfxInt32x2(ip);
    FfxFloat32x3 b = FsrRcasLoadF(sp + FfxInt32x2(0, -1)).rgb;
    FfxFloat32x3 d = FsrRcasLoadF(sp + FfxInt32x2(-1, 0)).rgb;
    FfxFloat32x3 e = FsrRcasLoadF(sp).rgb;
    FfxFloat32x3 f = FsrRcasLoadF(sp + FfxInt32x2(1, 0)).rgb;
    FfxFloat32x3 h = FsrRcasLoadF(sp + FfxInt32x2(0, 1)).rgb;

    FfxFloat32 bR = b.r; FfxFloat32 bG = b.g; FfxFloat32 bB = b.b;
    FfxFloat32 dR = d.r; FfxFloat32 dG = d.g; FfxFloat32 dB = d.b;
    FfxFloat32 eR = e.r; FfxFloat32 eG = e.g; FfxFloat32 eB = e.b;
    FfxFloat32 fR = f.r; FfxFloat32 fG = f.g; FfxFloat32 fB = f.b;
    FfxFloat32 hR = h.r; FfxFloat32 hG = h.g; FfxFloat32 hB = h.b;

    // Luma times 2.
    FfxFloat32 bL = bB * 0.5f + (bR * 0.5f + bG);
    FfxFloat32 dL = dB * 0.5f + (dR * 0.5f + dG);
    FfxFloat32 eL = eB * 0.5f + (eR * 0.5f + eG);
    FfxFloat32 fL = fB * 0.5f + (fR * 0.5f + fG);
    FfxFloat32 hL = hB * 0.5f + (hR * 0.5f + hG);

    // Noise detection.
    FfxFloat32 nz = 0.25f * bL + 0.25f * dL + 0.25f * fL + 0.25f * hL - eL;
    nz = ffxSaturate(abs(nz) * ffxApproximateReciprocalMedium(
        ffxMax3(ffxMax3(bL, dL, eL), fL, hL) - ffxMin3(ffxMin3(bL, dL, eL), fL, hL)));
    nz = -0.5f * nz + 1.0f;

    // Min and max of ring.
    FfxFloat32 mn4R = ffxMin(ffxMin3(bR, dR, fR), hR);
    FfxFloat32 mn4G = ffxMin(ffxMin3(bG, dG, fG), hG);
    FfxFloat32 mn4B = ffxMin(ffxMin3(bB, dB, fB), hB);
    FfxFloat32 mx4R = max(ffxMax3(bR, dR, fR), hR);
    FfxFloat32 mx4G = max(ffxMax3(bG, dG, fG), hG);
    FfxFloat32 mx4B = max(ffxMax3(bB, dB, fB), hB);

    // Immediate constants for peak range.
    FfxFloat32x2 peakC = FfxFloat32x2(1.0f, -1.0f * 4.0f);

    // Limiters, these need to be high precision RCPs.
    FfxFloat32 hitMinR = mn4R * ffxReciprocal(4.0f * mx4R);
    FfxFloat32 hitMinG = mn4G * ffxReciprocal(4.0f * mx4G);
    FfxFloat32 hitMinB = mn4B * ffxReciprocal(4.0f * mx4B);
    FfxFloat32 hitMaxR = (peakC.x - mx4R) * ffxReciprocal(4.0f * mn4R + peakC.y);
    FfxFloat32 hitMaxG = (peakC.x - mx4G) * ffxReciprocal(4.0f * mn4G + peakC.y);
    FfxFloat32 hitMaxB = (peakC.x - mx4B) * ffxReciprocal(4.0f * mn4B + peakC.y);
    FfxFloat32 lobeR = max(-hitMinR, hitMaxR);
    FfxFloat32 lobeG = max(-hitMinG, hitMaxG);
    FfxFloat32 lobeB = max(-hitMinB, hitMaxB);
    FfxFloat32 lobe = max(-FSR_RCAS_LIMIT, ffxMin(ffxMax3(lobeR, lobeG, lobeB), 0.0f)) *
        ffxAsFloat(con.x);

    // Apply noise removal (FSR_RCAS_DENOISE).
    lobe *= nz;

    // Resolve, which needs the medium precision rcp approximation to avoid visible
    // tonality changes.
    FfxFloat32 rcpL = ffxApproximateReciprocalMedium(4.0f * lobe + 1.0f);
    pixR = (lobe * bR + lobe * dR + lobe * hR + lobe * fR + eR) * rcpL;
    pixG = (lobe * bG + lobe * dG + lobe * hG + lobe * fG + eG) * rcpL;
    pixB = (lobe * bB + lobe * dB + lobe * hB + lobe * fB + eB) * rcpL;
}

void CurrFilter(FfxUInt32x2 position)
{
    // Deliberate deviation from upstream: dispatch rounds up to a 16-pixel tile, so
    // the last row and column of groups run past the output. Upstream leans on
    // out-of-bounds UAV writes being discarded, which both D3D12 and Metal do
    // guarantee, but an explicit guard costs nothing and keeps the pass from
    // depending on that.
    if (any(FfxInt32x2(position) >= UpscaleSize()))
    {
        return;
    }

    const FfxUInt32x4 config = RCASConfig();
    FfxFloat32x3 colour;
    if (config.y == 0u)
    {
        // Upstream writes the accumulation result directly when sharpening is disabled.
        // This pass-through reproduces that permutation without double-sharpening later.
        colour = LoadRCAS_Input(FfxInt32x2(position)).rgb;
    }
    else
    {
        FsrRcasF(colour.r, colour.g, colour.b, position, config);
        colour /= Exposure();
    }
    StoreUpscaledOutput(FfxInt32x2(position), colour);
}

// ---------------------------------------------------------------------------
// Entry point.
//
// 64 lanes per group, each group covering a 16x16 pixel tile: the lane index is
// quad-swizzled into an 8x8 block for fetch locality, then the block is repeated at
// +8 in x and y. Dispatch is therefore ceil(upscaleSize / 16), NOT the engine's usual
// ComputeThreadGroupSize-derived count - see Fsr3RcasPass.Record.
// ---------------------------------------------------------------------------

[numthreads(64, 1, 1)]
void Fsr3RcasCS(
    uint3 localThreadId : SV_GroupThreadID,
    uint3 workGroupId : SV_GroupID,
    uint3 dispatchThreadId : SV_DispatchThreadID)
{
    FfxUInt32x2 gxy = ffxRemapForQuad(localThreadId.x) +
        FfxUInt32x2(workGroupId.x << 4u, workGroupId.y << 4u);

    CurrFilter(gxy);
    gxy.x += 8u;
    CurrFilter(gxy);
    gxy.y += 8u;
    CurrFilter(gxy);
    gxy.x -= 8u;
    CurrFilter(gxy);
}
