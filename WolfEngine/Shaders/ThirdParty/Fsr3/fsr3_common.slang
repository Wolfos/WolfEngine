// Shared FSR3 upscaler helpers.
//
// Ported from ffx-fsr3/upstream/ffx_fsr3upscaler_common.h (FidelityFX SDK v1.1.2).
// Only the parts the currently ported passes reference are here; the file grows as
// each further pass lands rather than being translated wholesale up front.
//
// Everything is fp32. Where upstream branches on FFX_HALF this port takes the fp32
// arm unconditionally.

#ifndef WOLF_FSR3_COMMON_INCLUDED
#define WOLF_FSR3_COMMON_INCLUDED

import "ffx_port_common.slang";
import "fsr3_constants.slang";

static const float FSR3UPSCALER_FP16_MIN = 6.10e-05f;
static const float FSR3UPSCALER_FP16_MAX = 65504.0f;
static const float FSR3UPSCALER_EPSILON = FSR3UPSCALER_FP16_MIN;
static const float FSR3UPSCALER_TONEMAP_EPSILON = FSR3UPSCALER_FP16_MIN;
static const float FSR3UPSCALER_FP32_MAX = 3.402823466e+38f;
static const float FSR3UPSCALER_FP32_MIN = 1.175494351e-38f;

// Reconstructed depth usage.
static const float fReconstructedDepthBilinearWeightThreshold = FSR3UPSCALER_EPSILON * 10.0f;

// Channel indices into the 1x1 frame-info target written at the top of the luma pyramid.
static const int FRAME_INFO_EXPOSURE = 0;
static const int FRAME_INFO_LOG_LUMA = 1;
static const int FRAME_INFO_SCENE_AVERAGE_LUMA = 2;

// Channel indices in the packed dilated-reactive-mask target. Keep upstream's
// ACCUMULAION spelling so ported pass bodies remain directly diffable.
static const int REACTIVE = 0;
static const int DISOCCLUSION = 1;
static const int SHADING_CHANGE = 2;
static const int ACCUMULAION = 3;

static const FfxFloat32 fLockThreshold = 1.0f;
static const FfxFloat32 fLockMax = 2.0f;
// Keep the 1/16 normalization from upstream's fUpsampleLanczosWeightScale. Using the
// unscaled AverageLanczosWeightPerFrame() helper value here makes every new jittered
// sample sixteen times too influential and prevents static detail from converging.
static const FfxFloat32 fUpsampleLanczosWeightScale = 1.0f / 16.0f;
static const FfxFloat32 fAverageLanczosWeightPerFrame = 0.74f * fUpsampleLanczosWeightScale;
// FSR 3.1.4 defaults. A disocclusion stores zero accumulation this frame and delays
// rebuilding temporal confidence by one additional frame, preventing newly revealed
// pixels from immediately accepting stale history.
static const FfxFloat32 fAccumulationAddedPerFrame = 1.0f / 3.0f;
static const FfxFloat32 fMinDisocclusionAccumulation = -fAccumulationAddedPerFrame;

struct AccumulationPassCommonParams
{
    FfxInt32x2 iPxHrPos;
    FfxFloat32x2 fHrUv;
    FfxFloat32x2 fLrUvJittered;
    FfxFloat32x2 fLrUv_HwSampler;
    FfxFloat32x2 fMotionVector;
    FfxFloat32x2 fReprojectedHrUv;
    FfxFloat32 f4KVelocity;
    FfxFloat32 fDisocclusion;
    FfxFloat32 fReactiveMask;
    FfxFloat32 fShadingChange;
    FfxFloat32 fAccumulation;
    FfxFloat32 fLumaInstabilityFactor;
    FfxFloat32 fFarthestDepthInMeters;
    FfxBoolean bIsExistingSample;
    FfxBoolean bIsNewSample;
};

struct RectificationBox
{
    FfxFloat32x3 boxCenter;
    FfxFloat32x3 boxVec;
    FfxFloat32x3 aabbMin;
    FfxFloat32x3 aabbMax;
    FfxFloat32 fBoxCenterWeight;
};

struct AccumulationPassData
{
    RectificationBox clippingBox;
    FfxFloat32x3 fUpsampledColor;
    FfxFloat32 fUpsampledWeight;
    FfxFloat32x3 fHistoryColor;
    FfxFloat32 fHistoryWeight;
    FfxFloat32 fLock;
    FfxFloat32 fLockContributionThisFrame;
};

void RectificationBoxAddSample(FfxBoolean initial, inout RectificationBox box,
    FfxFloat32x3 sample, FfxFloat32 weight)
{
    FfxFloat32x3 weightedSample = sample * weight;
    if (initial)
    {
        box.aabbMin = sample;
        box.aabbMax = sample;
        box.boxCenter = weightedSample;
        box.boxVec = sample * weightedSample;
        box.fBoxCenterWeight = weight;
    }
    else
    {
        box.aabbMin = ffxMin(box.aabbMin, sample);
        box.aabbMax = ffxMax(box.aabbMax, sample);
        box.boxCenter += weightedSample;
        box.boxVec += sample * weightedSample;
        box.fBoxCenterWeight += weight;
    }
}

void RectificationBoxComputeVarianceBoxData(inout RectificationBox box)
{
    box.fBoxCenterWeight = abs(box.fBoxCenterWeight) > FSR3UPSCALER_FP32_MIN
        ? box.fBoxCenterWeight : 1.0f;
    box.boxCenter /= box.fBoxCenterWeight;
    box.boxVec /= box.fBoxCenterWeight;
    box.boxVec = sqrt(abs(box.boxVec - box.boxCenter * box.boxCenter));
}

FfxFloat32x3 YCoCgToRGB(FfxFloat32x3 value)
{
    return FfxFloat32x3(value.x + value.y - value.z,
        value.x + value.z, value.x - value.y - value.z);
}

FfxFloat32x3 RGBToYCoCg(FfxFloat32x3 value)
{
    return FfxFloat32x3(0.25f * value.r + 0.5f * value.g + 0.25f * value.b,
        0.5f * value.r - 0.5f * value.b,
        -0.25f * value.r + 0.5f * value.g - 0.25f * value.b);
}

FfxFloat32x3 Tonemap(FfxFloat32x3 value)
{
    return value / (ffxMax(ffxMax(0.0f, value.r), ffxMax(value.g, value.b)) + 1.0f);
}

FfxFloat32x3 InverseTonemap(FfxFloat32x3 value)
{
    return value / ffxMax(FSR3UPSCALER_TONEMAP_EPSILON,
        1.0f - ffxMax(value.r, ffxMax(value.g, value.b)));
}

/// Above this, the previous frame's log luma is treated as uninitialised and adopted
/// outright rather than smoothed towards. Large enough that no real scene reaches it.
static const float resetAutoExposureAverageSmoothing = 1e4f;

/// Converts an average log luminance into an exposure multiplier, via the ISO 100
/// saturation-based speed relation upstream uses.
FfxFloat32 ComputeAutoExposureFromLavg(FfxFloat32 Lavg)
{
    Lavg = exp(Lavg);

    const FfxFloat32 S = 100.0f; // ISO arithmetic speed
    const FfxFloat32 K = 12.5f;
    FfxFloat32 ExposureISO100 = log2((Lavg * S) / K);

    const FfxFloat32 q = 0.65f;
    FfxFloat32 Lmax = (78.0f / (q * S)) * ffxPow(2.0f, ExposureISO100);

    return 1.0f / Lmax;
}

/// Motion below this many pixels (measured at 4K) is treated as stationary when
/// scattering depth into the previous frame, so that sub-pixel jitter does not smear
/// the reconstructed depth across neighbours.
FfxFloat32 ReconstructedDepthMvPxThreshold(FfxFloat32 fNearestDepthInMeters)
{
    return ffxLerp(0.25f, 0.75f, ffxSaturate(fNearestDepthInMeters / 100.0f));
}

/// Motion vector length expressed in 4K pixels, so thresholds are resolution independent.
FfxFloat32 Get4KVelocity(FfxFloat32x2 fMotionVector)
{
    return length(fMotionVector * FfxFloat32x2(3840.0f, 2160.0f));
}

FfxFloat32 RGBToLuma(FfxFloat32x3 fLinearRgb)
{
    return dot(fLinearRgb, FfxFloat32x3(0.2126f, 0.7152f, 0.0722f));
}

FfxBoolean IsOnScreen(FfxInt32x2 pos, FfxInt32x2 size)
{
    // Upstream relies on the unsigned wrap of a negative coordinate exceeding size,
    // which folds the lower bound test into the upper one.
    return all(FfxUInt32x2(pos) < FfxUInt32x2(size));
}

FfxBoolean IsUvInside(FfxFloat32x2 fUv)
{
    return (fUv.x >= 0.0f && fUv.x <= 1.0f) && (fUv.y >= 0.0f && fUv.y <= 1.0f);
}

FfxInt32x2 ClampLoad(FfxInt32x2 iPxSample, FfxInt32x2 iPxOffset, FfxInt32x2 iTextureSize)
{
    FfxInt32x2 result = iPxSample + iPxOffset;
    result.x = ffxMax(0, ffxMin(result.x, iTextureSize.x - 1));
    result.y = ffxMax(0, ffxMin(result.y, iTextureSize.y - 1));
    return result;
}

/// Clamps a UV to the half-texel inset of a sub-rectangle of a larger resource, so a
/// bilinear tap cannot reach outside the region actually written.
FfxFloat32x2 ClampUv(FfxFloat32x2 fUv, FfxInt32x2 iTextureSize, FfxInt32x2 iResourceSize)
{
    const FfxFloat32x2 fSampleLocation = fUv * iTextureSize;
    const FfxFloat32x2 fClampedLocation = ffxMax(
        FfxFloat32x2(0.5f, 0.5f),
        ffxMin(fSampleLocation, FfxFloat32x2(iTextureSize) - FfxFloat32x2(0.5f, 0.5f)));
    return fClampedLocation / FfxFloat32x2(iResourceSize);
}

/// Ratio of the smaller to the larger of two values, a scale-invariant similarity measure.
FfxFloat32 MinDividedByMax(const FfxFloat32 v0, const FfxFloat32 v1, const FfxFloat32 fOnZeroReturnValue)
{
    const FfxFloat32 m = ffxMax(v0, v1);
    return m != 0 ? ffxMin(v0, v1) / m : fOnZeroReturnValue;
}

FfxFloat32 MinDividedByMax(const FfxFloat32 v0, const FfxFloat32 v1)
{
    return MinDividedByMax(v0, v1, 0.0f);
}

// ---------------------------------------------------------------------------
// Shading change
//
// The shading-change pyramid runs at half render resolution: its SPD mip 0 is already a
// 2x reduction of the render-resolution luma.
// ---------------------------------------------------------------------------

// A static const rather than upstream's #define: preprocessor macros do not cross Slang
// module boundaries, and this is imported rather than textually included.
static const FfxInt32 SHADING_CHANGE_SET_SIZE = 5;
static const FfxInt32 iShadingChangeMipStart = 0;
static const FfxFloat32 fShadingChangeSamplePow = 1.0f / 1.0f;

FfxInt32x2 ShadingChangeRenderSize() { return FfxInt32x2(RenderSize() * 0.5f); }
FfxInt32x2 ShadingChangeMaxRenderSize() { return FfxInt32x2(MaxRenderSize() * 0.5f); }
FfxInt32x2 PreviousFrameShadingChangeRenderSize() { return FfxInt32x2(PreviousFrameRenderSize() * 0.5f); }

// ---------------------------------------------------------------------------
// Depth
//
// fDeviceToViewDepth is derived on the CPU from the projection's near/far and the
// inverted/infinite flags - see Fsr3Constants.BuildDeviceToViewDepth and
// ffx_fsr3upscaler.cpp.
// ---------------------------------------------------------------------------

FfxFloat32 GetViewSpaceDepth(FfxFloat32 fDeviceDepth)
{
    const FfxFloat32x4 fDeviceToViewDepth = DeviceToViewSpaceTransformFactors();
    return fDeviceToViewDepth[1] / (fDeviceDepth - fDeviceToViewDepth[0]);
}

FfxFloat32 GetViewSpaceDepthInMeters(FfxFloat32 fDeviceDepth)
{
    return GetViewSpaceDepth(fDeviceDepth) * ViewSpaceToMetersFactor();
}

FfxFloat32x2 ComputeNdc(FfxFloat32x2 fPxPos, FfxInt32x2 iSize)
{
    return fPxPos / FfxFloat32x2(iSize) * FfxFloat32x2(2.0f, -2.0f) + FfxFloat32x2(-1.0f, 1.0f);
}

// ---------------------------------------------------------------------------
// Resolution mapping
// ---------------------------------------------------------------------------

/// Maps a render-resolution pixel to the display-resolution pixel its jittered sample
/// actually landed on. At a 1.0 ratio this is not the identity: the jitter offset still
/// applies, which is what makes native-AA mode work through the same code path.
FfxInt32x2 ComputeHrPosFromLrPos(FfxInt32x2 iPxLrPos)
{
    FfxFloat32x2 fSrcJitteredPos = FfxFloat32x2(iPxLrPos) + 0.5f - Jitter();
    FfxFloat32x2 fLrPosInHr = (fSrcJitteredPos / RenderSize()) * UpscaleSize();
    FfxInt32x2 iPxHrPos = FfxInt32x2(floor(fLrPosInHr));
    return iPxHrPos;
}

// ---------------------------------------------------------------------------
// Bilinear footprint
// ---------------------------------------------------------------------------

struct BilinearSamplingData
{
    FfxInt32x2 iOffsets[4];
    FfxFloat32 fWeights[4];
    FfxInt32x2 iBasePos;
};

BilinearSamplingData GetBilinearSamplingData(FfxFloat32x2 fUv, FfxInt32x2 iSize)
{
    BilinearSamplingData data;

    FfxFloat32x2 fPxSample = (fUv * iSize) - FfxFloat32x2(0.5f, 0.5f);
    data.iBasePos = FfxInt32x2(floor(fPxSample));
    FfxFloat32x2 fPxFrac = ffxFract(fPxSample);

    data.iOffsets[0] = FfxInt32x2(0, 0);
    data.iOffsets[1] = FfxInt32x2(1, 0);
    data.iOffsets[2] = FfxInt32x2(0, 1);
    data.iOffsets[3] = FfxInt32x2(1, 1);

    data.fWeights[0] = (1 - fPxFrac.x) * (1 - fPxFrac.y);
    data.fWeights[1] = (fPxFrac.x) * (1 - fPxFrac.y);
    data.fWeights[2] = (1 - fPxFrac.x) * (fPxFrac.y);
    data.fWeights[3] = (fPxFrac.x) * (fPxFrac.y);

    return data;
}

#endif // WOLF_FSR3_COMMON_INCLUDED
