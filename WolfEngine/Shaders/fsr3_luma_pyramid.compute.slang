// FSR3 upscaler - luma pyramid pass.
//
// Ported from ffx-fsr3/upstream/ffx_fsr3upscaler_luma_pyramid.h (FidelityFX SDK v1.1.2),
// with the single-pass downsample core in Shaders/fsr3_spd.slang.
//
// Reduces prepare_inputs' luma and farthest-depth outputs into a mip chain, and at the
// 1x1 level derives the frame's auto exposure. That exposure is what RCAS and accumulate
// consume, so this pass is what takes the effect off a placeholder exposure value.
//
// Three things ride on one reduction:
//   .x  log luma   -> auto exposure at the 1x1 level
//   .y  luma       -> scene average luma at the 1x1 level
//   .z  farthest depth in metres -> written out at mip 1 only
// Only .xy are stored into the pyramid; .z exists to get the depth to mip 1.
//
// This is the fp32 path. Upstream's FFX_HALF SPD callbacks are stubs that return zero
// even in the reference, so nothing is lost by omitting them.

import "common_bindless.slang";
import "ffx_port_common.slang";
import "fsr3_constants.slang";
import "fsr3_common.slang";
import "fsr3_callbacks.slang";

// Mirrors cbSPD from ffx_fsr3upscaler_callbacks_hlsl.h. The uint2 pairs are declared as
// scalars for the same reason as in fsr3_constants.slang: ShaderPropertyWriter has no
// integer-vector setter. Packing is unchanged.
cbuffer cbSPD : register(b2)
{
    uint spdMips;
    uint spdNumWorkGroups;
    uint spdWorkGroupOffsetX;
    uint spdWorkGroupOffsetY;
    uint spdRenderSizeX;
    uint spdRenderSizeY;
};

FfxUInt32 MipCount() { return spdMips; }
FfxUInt32 NumWorkGroups() { return spdNumWorkGroups; }
FfxUInt32x2 WorkGroupOffset() { return FfxUInt32x2(spdWorkGroupOffsetX, spdWorkGroupOffsetY); }
FfxUInt32x2 SpdRenderSize() { return FfxUInt32x2(spdRenderSizeX, spdRenderSizeY); }

// ---------------------------------------------------------------------------
// SPD callbacks. These must be declared before fsr3_spd.slang is included - that file is
// a textual include precisely so it can bind to them.
// ---------------------------------------------------------------------------

groupshared FfxUInt32 spdCounter;

groupshared FfxFloat32 spdIntermediateR[16][16];
groupshared FfxFloat32 spdIntermediateG[16][16];
groupshared FfxFloat32 spdIntermediateB[16][16];
groupshared FfxFloat32 spdIntermediateA[16][16];

static const FfxInt32 LOG_LUMA = 0;
static const FfxInt32 LUMA = 1;
static const FfxInt32 DEPTH_IN_METERS = 2;

void SpdIncreaseAtomicCounter(FfxUInt32 slice)
{
    SPD_IncreaseAtomicCounter(spdCounter);
}

FfxUInt32 SpdGetAtomicCounter()
{
    return spdCounter;
}

void SpdResetAtomicCounter(FfxUInt32 slice)
{
    SPD_ResetAtomicCounter();
}

FfxFloat32x4 SpdLoadSourceImage(FfxFloat32x2 iPxPos, FfxUInt32 slice)
{
    const FfxInt32x2 iPxSamplePos = ClampLoad(
        FfxInt32x2(iPxPos),
        FfxInt32x2(0, 0),
        FfxInt32x2(RenderSize()));

    const FfxFloat32 fLuma = LoadCurrentLuma(iPxSamplePos);
    const FfxFloat32 fLogLuma = ffxMax(FSR3UPSCALER_EPSILON, log(fLuma));
    const FfxFloat32 fFarthestDepthInMeters = LoadFarthestDepth(iPxSamplePos);

    FfxFloat32x4 fOutput = FfxFloat32x4(0.0f, 0.0f, 0.0f, 0.0f);
    fOutput[LOG_LUMA] = fLogLuma;
    fOutput[LUMA] = fLuma;
    fOutput[DEPTH_IN_METERS] = fFarthestDepthInMeters;

    return fOutput;
}

FfxFloat32x4 SpdLoad(FfxInt32x2 tex, FfxUInt32 slice)
{
    // Only mip 5 is ever read back, and only by the elected last workgroup.
    return FfxFloat32x4(RWLoadPyramid(tex, 5), 0.0f, 0.0f);
}

FfxFloat32x4 SpdReduce4(FfxFloat32x4 v0, FfxFloat32x4 v1, FfxFloat32x4 v2, FfxFloat32x4 v3)
{
    return (v0 + v1 + v2 + v3) * 0.25f;
}

void SpdStore(FfxInt32x2 pix, FfxFloat32x4 outValue, FfxUInt32 index, FfxUInt32 slice)
{
    if (index == 5)
    {
        StorePyramid(pix, outValue.xy, index);
    }
    else if (index == 0)
    {
        StoreFarthestDepthMip1(pix, outValue[DEPTH_IN_METERS]);
    }

    if (index == MipCount() - 1)
    {
        // The 1x1 level. A single lane in a single group reaches here, which is what makes
        // it safe to read-modify-write the frame info without any further synchronisation.
        if (all(pix == FfxInt32x2(0, 0)))
        {
            FfxFloat32x4 frameInfo = LoadFrameInfo();
            const FfxFloat32 fSceneAvgLuma = outValue[LUMA];
            const FfxFloat32 fPrevLogLuma = frameInfo[FRAME_INFO_LOG_LUMA];
            FfxFloat32 fLogLuma = outValue[LOG_LUMA];

            // Smooth towards the new value at a rate set by frame time, unless the previous
            // value is out of range, which marks an uninitialised or reset history.
            if (fPrevLogLuma < resetAutoExposureAverageSmoothing)
            {
                fLogLuma = fPrevLogLuma + (fLogLuma - fPrevLogLuma) * (1.0f - exp(-DeltaTime()));
                fLogLuma = ffxMax(0.0f, fLogLuma);
            }

            frameInfo[FRAME_INFO_EXPOSURE] = ComputeAutoExposureFromLavg(fLogLuma);
            frameInfo[FRAME_INFO_LOG_LUMA] = fLogLuma;
            frameInfo[FRAME_INFO_SCENE_AVERAGE_LUMA] = fSceneAvgLuma;

            StoreFrameInfo(frameInfo);
        }
    }
}

FfxFloat32x4 SpdLoadIntermediate(FfxUInt32 x, FfxUInt32 y)
{
    return FfxFloat32x4(
        spdIntermediateR[x][y],
        spdIntermediateG[x][y],
        spdIntermediateB[x][y],
        spdIntermediateA[x][y]);
}

void SpdStoreIntermediate(FfxUInt32 x, FfxUInt32 y, FfxFloat32x4 value)
{
    spdIntermediateR[x][y] = value.x;
    spdIntermediateG[x][y] = value.y;
    spdIntermediateB[x][y] = value.z;
    spdIntermediateA[x][y] = value.w;
}

#include "fsr3_spd.slang"

void ComputeAutoExposure(FfxUInt32x3 workGroupId, FfxUInt32 localThreadIndex)
{
    SpdDownsample(
        FfxUInt32x2(workGroupId.xy),
        FfxUInt32(localThreadIndex),
        FfxUInt32(MipCount()),
        FfxUInt32(NumWorkGroups()),
        FfxUInt32(workGroupId.z),
        FfxUInt32x2(WorkGroupOffset()));
}

// Dispatch is one group per 64x64 source tile - see Fsr3LumaPyramidPass.Record.
[numthreads(256, 1, 1)]
void Fsr3LumaPyramidCS(
    uint3 workGroupId : SV_GroupID,
    uint localThreadIndex : SV_GroupIndex)
{
    ComputeAutoExposure(workGroupId, localThreadIndex);
}
