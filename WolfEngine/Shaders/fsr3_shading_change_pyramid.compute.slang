// FSR3 upscaler - shading change pyramid pass.
//
// Ported from ffx-fsr3/upstream/ffx_fsr3upscaler_shading_change_pyramid.h
// (FidelityFX SDK v1.1.2), reusing the single-pass downsample in Shaders/fsr3_spd.slang.
//
// Per render-resolution pixel it reprojects into last frame's luma and measures how much
// the shading changed, independently of geometry motion. That signal is then reduced into
// a pyramid so the shading_change pass can look at it across scales: a lighting change
// large enough to matter shows up at several levels, while temporal noise does not.
//
// The difference is deliberately scale-invariant - MinDividedByMax rather than a
// subtraction - so a bright surface and a dim one changing by the same proportion score
// the same.
//
// Note that SPD mip 0 here is already half render resolution, which is why the whole
// shading-change chain runs at ShadingChangeRenderSize.

import "common_bindless.slang";
import "ffx_port_common.slang";
import "fsr3_constants.slang";
import "fsr3_common.slang";
import "fsr3_callbacks.slang";

// Mirrors cbSPD - see fsr3_luma_pyramid.compute.slang for why the uint2s are scalars.
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

// ---------------------------------------------------------------------------
// Luma difference against the reprojected previous frame
// ---------------------------------------------------------------------------

static const FfxInt32 DIFFERENCE = 0;
static const FfxInt32 SIGN_SUM = 1;
static const FfxInt32 MIP0_INDICATOR = 2;

static const FfxInt32x2 iSampleOffsets[5] = {
    FfxInt32x2(+0, +0),
    FfxInt32x2(-1, +0),
    FfxInt32x2(+1, +0),
    FfxInt32x2(+0, -1),
    FfxInt32x2(+0, +1)
};

struct SampleSet
{
    FfxFloat32 fSamples[SHADING_CHANGE_SET_SIZE];
};

// Sorting network for 5 elements, as upstream's CompareSwap macro chain.
void SortSet(inout SampleSet fSet)
{
    FfxFloat32 fTmp;

    #define WOLF_COMPARE_SWAP(i, j)                                          \
    {                                                                        \
        fTmp = ffxMin(fSet.fSamples[i], fSet.fSamples[j]);                   \
        fSet.fSamples[j] = ffxMax(fSet.fSamples[i], fSet.fSamples[j]);       \
        fSet.fSamples[i] = fTmp;                                             \
    }

    WOLF_COMPARE_SWAP(0, 3);
    WOLF_COMPARE_SWAP(1, 4);
    WOLF_COMPARE_SWAP(0, 2);
    WOLF_COMPARE_SWAP(1, 3);
    WOLF_COMPARE_SWAP(0, 1);
    WOLF_COMPARE_SWAP(2, 4);
    WOLF_COMPARE_SWAP(1, 2);
    WOLF_COMPARE_SWAP(3, 4);
    WOLF_COMPARE_SWAP(2, 3);

    #undef WOLF_COMPARE_SWAP
}

/// Walks the two sorted sets together and keeps the smallest signed relative difference.
/// Taking the minimum rather than the centre sample is what makes this robust to a single
/// mismatched tap at a silhouette.
FfxFloat32 ComputeMinimumDifference(FfxInt32x2 iPxPos, SampleSet fSet0, SampleSet fSet1)
{
    FfxFloat32 fMinDiff = FSR3UPSCALER_FP16_MAX - 1;
    FfxInt32 a = 0;
    FfxInt32 b = 0;

    SortSet(fSet0);
    SortSet(fSet1);

    const FfxFloat32 fMax = ffxMin(
        fSet0.fSamples[SHADING_CHANGE_SET_SIZE - 1],
        fSet1.fSamples[SHADING_CHANGE_SET_SIZE - 1]);

    if (fMax > FSR3UPSCALER_FP32_MIN)
    {
        [unroll]
        for (FfxInt32 i = 0; i < SHADING_CHANGE_SET_SIZE && (fMinDiff < FSR3UPSCALER_FP16_MAX); i++)
        {
            FfxFloat32 fDiff = fSet0.fSamples[a] - fSet1.fSamples[b];

            if (abs(fDiff) > FSR3UPSCALER_FP16_MIN)
            {
                fDiff = sign(fDiff) * (1.0f - MinDividedByMax(fSet0.fSamples[a], fSet1.fSamples[b]));

                fMinDiff = (abs(fDiff) < abs(fMinDiff)) ? fDiff : fMinDiff;

                a += FfxInt32(fSet0.fSamples[a] < fSet1.fSamples[b]);
                b += FfxInt32(fSet0.fSamples[a] >= fSet1.fSamples[b]);
            }
            else
            {
                fMinDiff = FSR3UPSCALER_FP16_MAX;
            }
        }
    }

    return fMinDiff * FfxFloat32(fMinDiff < (FSR3UPSCALER_FP16_MAX - 1));
}

SampleSet GetCurrentLumaBilinearSamples(FfxFloat32x2 fUv)
{
    const FfxFloat32x2 fUvJittered = fUv + Jitter() / RenderSize();
    const FfxInt32x2 iBasePos = FfxInt32x2(floor(fUvJittered * RenderSize()));

    SampleSet fSet;

    for (FfxInt32 iSampleIndex = 0; iSampleIndex < SHADING_CHANGE_SET_SIZE; iSampleIndex++)
    {
        const FfxInt32x2 iSamplePos = ClampLoad(iBasePos, iSampleOffsets[iSampleIndex], RenderSize());
        fSet.fSamples[iSampleIndex] = LoadCurrentLuma(iSamplePos) * Exposure();
        fSet.fSamples[iSampleIndex] = ffxPow(fSet.fSamples[iSampleIndex], fShadingChangeSamplePow);
        fSet.fSamples[iSampleIndex] = ffxMax(fSet.fSamples[iSampleIndex], FSR3UPSCALER_EPSILON);
    }

    return fSet;
}

struct PreviousLumaBilinearSamplesData
{
    SampleSet fSet;
    FfxBoolean bIsExistingSample;
};

PreviousLumaBilinearSamplesData GetPreviousLumaBilinearSamples(
    FfxFloat32x2 fUv,
    FfxFloat32x2 fMotionVector)
{
    PreviousLumaBilinearSamplesData data;

    const FfxFloat32x2 fUvJittered = fUv + PreviousFrameJitter() / PreviousFrameRenderSize();
    const FfxFloat32x2 fReprojectedUv = fUvJittered + fMotionVector;

    data.bIsExistingSample = IsUvInside(fReprojectedUv);

    if (data.bIsExistingSample)
    {
        const FfxInt32x2 iBasePos = FfxInt32x2(floor(fReprojectedUv * PreviousFrameRenderSize()));

        for (FfxInt32 iSampleIndex = 0; iSampleIndex < SHADING_CHANGE_SET_SIZE; iSampleIndex++)
        {
            const FfxInt32x2 iSamplePos = ClampLoad(
                iBasePos,
                iSampleOffsets[iSampleIndex],
                PreviousFrameRenderSize());
            // DeltaPreExposure brings last frame's luma into this frame's exposure, so an
            // exposure change alone does not read as a shading change.
            data.fSet.fSamples[iSampleIndex] = LoadPreviousLuma(iSamplePos) * DeltaPreExposure() * Exposure();
            data.fSet.fSamples[iSampleIndex] = ffxPow(data.fSet.fSamples[iSampleIndex], fShadingChangeSamplePow);
            data.fSet.fSamples[iSampleIndex] = ffxMax(data.fSet.fSamples[iSampleIndex], FSR3UPSCALER_EPSILON);
        }
    }

    return data;
}

FfxFloat32 ComputeDiff(FfxInt32x2 iPxPos, FfxFloat32x2 fUv, FfxFloat32x2 fMotionVector)
{
    FfxFloat32 fMinDiff = 0.0f;

    const SampleSet fCurrentSamples = GetCurrentLumaBilinearSamples(fUv);
    const PreviousLumaBilinearSamplesData previousData = GetPreviousLumaBilinearSamples(fUv, fMotionVector);

    if (previousData.bIsExistingSample)
    {
        fMinDiff = ComputeMinimumDifference(iPxPos, fCurrentSamples, previousData.fSet);
    }

    return fMinDiff;
}

// ---------------------------------------------------------------------------
// SPD callbacks
// ---------------------------------------------------------------------------

groupshared FfxUInt32 spdCounter;

groupshared FfxFloat32 spdIntermediateR[16][16];
groupshared FfxFloat32 spdIntermediateG[16][16];
groupshared FfxFloat32 spdIntermediateB[16][16];
groupshared FfxFloat32 spdIntermediateA[16][16];

void SpdIncreaseAtomicCounter(FfxUInt32 slice) { SPD_IncreaseAtomicCounter(spdCounter); }
FfxUInt32 SpdGetAtomicCounter() { return spdCounter; }
void SpdResetAtomicCounter(FfxUInt32 slice) { SPD_ResetAtomicCounter(); }

FfxFloat32x4 SpdLoadSourceImage(FfxFloat32x2 iPxPos, FfxUInt32 slice)
{
    const FfxInt32x2 iPxSamplePos = ClampLoad(
        FfxInt32x2(iPxPos),
        FfxInt32x2(0, 0),
        FfxInt32x2(RenderSize()));
    const FfxFloat32x2 fDilatedMotionVector = LoadDilatedMotionVector(iPxSamplePos);
    const FfxFloat32x2 fUv = (iPxSamplePos + 0.5f) / RenderSize();

    const FfxFloat32 fScaledAndSignedLumaDiff = ComputeDiff(iPxSamplePos, fUv, fDilatedMotionVector);

    FfxFloat32x4 fOutput = FfxFloat32x4(0.0f, 0.0f, 0.0f, 0.0f);
    fOutput[DIFFERENCE] = fScaledAndSignedLumaDiff;
    // Summing the signs separately lets a coarser level tell a genuine one-directional
    // lighting change from noise that cancels out.
    fOutput[SIGN_SUM] = (fScaledAndSignedLumaDiff != 0.0f) ? sign(fScaledAndSignedLumaDiff) : 0.0f;
    fOutput[MIP0_INDICATOR] = 1.0f;

    return fOutput;
}

FfxFloat32x4 SpdLoad(FfxInt32x2 tex, FfxUInt32 slice)
{
    return FfxFloat32x4(RWLoadPyramid(tex, 5), 0.0f, 0.0f);
}

FfxFloat32x4 SpdReduce4(FfxFloat32x4 v0, FfxFloat32x4 v1, FfxFloat32x4 v2, FfxFloat32x4 v3)
{
    return (v0 + v1 + v2 + v3) * 0.25f;
}

void SpdStore(FfxInt32x2 pix, FfxFloat32x4 outValue, FfxUInt32 index, FfxUInt32 slice)
{
    if (index >= FfxUInt32(iShadingChangeMipStart))
    {
        StorePyramid(pix, outValue.xy, index);
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

void ComputeShadingChangePyramid(FfxUInt32x3 workGroupId, FfxUInt32 localThreadIndex)
{
    SpdDownsample(
        FfxUInt32x2(workGroupId.xy),
        FfxUInt32(localThreadIndex),
        FfxUInt32(MipCount()),
        FfxUInt32(NumWorkGroups()),
        FfxUInt32(workGroupId.z),
        FfxUInt32x2(WorkGroupOffset()));
}

// Dispatch is one group per 64x64 render-resolution tile, as for the luma pyramid.
[numthreads(256, 1, 1)]
void Fsr3ShadingChangePyramidCS(
    uint3 workGroupId : SV_GroupID,
    uint localThreadIndex : SV_GroupIndex)
{
    ComputeShadingChangePyramid(workGroupId, localThreadIndex);
}
