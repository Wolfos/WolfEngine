// FSR3 upscaler - accumulation pass.
// Ported from ffx_fsr3upscaler_accumulate.h, upsample.h, reproject.h and sample.h
// (FidelityFX SDK v1.1.2). The fp16/Xbox twins are intentionally omitted.

import "../../Common/common_bindless.slang";
import "ffx_port_common.slang";
import "fsr3_constants.slang";
import "fsr3_common.slang";
import "fsr3_callbacks.slang";

FfxFloat32 Lanczos2ApproxSq(FfxFloat32 x2)
{
    x2 = ffxMin(x2, 4.0f);
    FfxFloat32 a = (2.0f / 5.0f) * x2 - 1.0f;
    FfxFloat32 b = (1.0f / 4.0f) * x2 - 1.0f;
    return ((25.0f / 16.0f) * a * a - (25.0f / 16.0f - 1.0f)) * b * b;
}

FfxFloat32 Lanczos2(FfxFloat32 x)
{
    x = abs(x);
    if (x < 1.0e-5f) return 1.0f;
    if (x >= 2.0f) return 0.0f;
    const FfxFloat32 piX = 3.14159265358979323846f * x;
    return (sin(piX) / piX) * (sin(piX * 0.5f) / (piX * 0.5f));
}

FfxFloat32x4 HistorySample(FfxFloat32x2 uv, FfxInt32x2 size)
{
    FfxFloat32x2 position = uv * size - 0.5f;
    FfxInt32x2 basePixel = FfxInt32x2(floor(position));
    FfxFloat32x2 fraction = position - floor(position);
    FfxFloat32x4 value = 0.0f;
    FfxFloat32 totalWeight = 0.0f;
    [unroll]
    for (FfxInt32 y = -1; y <= 2; ++y)
    {
        [unroll]
        for (FfxInt32 x = -1; x <= 2; ++x)
        {
            FfxFloat32 weight = Lanczos2(FfxFloat32(x) - fraction.x) *
                Lanczos2(FfxFloat32(y) - fraction.y);
            value += LoadHistory(basePixel + FfxInt32x2(x, y)) * weight;
            totalWeight += weight;
        }
    }

    value /= ffxMax(abs(totalWeight), FSR3UPSCALER_EPSILON);

    // Match the reference HistorySample deringing step. The outer Lanczos lobes can
    // otherwise pull a high-contrast foreground sample into a reprojected background
    // pixel even after the foreground has moved away. Restrict the result to the range
    // of the four bilinear taps surrounding the sample position so those lobes retain
    // sharpness without creating a smooth temporal trail at disocclusion boundaries.
    const FfxFloat32x4 center00 = LoadHistory(basePixel + FfxInt32x2(0, 0));
    const FfxFloat32x4 center10 = LoadHistory(basePixel + FfxInt32x2(1, 0));
    const FfxFloat32x4 center01 = LoadHistory(basePixel + FfxInt32x2(0, 1));
    const FfxFloat32x4 center11 = LoadHistory(basePixel + FfxInt32x2(1, 1));
    const FfxFloat32x4 deringingMin = ffxMin(
        ffxMin(center00, center10), ffxMin(center01, center11));
    const FfxFloat32x4 deringingMax = ffxMax(
        ffxMax(center00, center10), ffxMax(center01, center11));

    return clamp(value, deringingMin, deringingMax);
}

FfxFloat32 GetUpsampleLanczosWeight(FfxFloat32x2 offset, FfxFloat32 kernelWeight)
{
    FfxFloat32x2 biased = offset * kernelWeight;
    // Engine permutation: approximate Lanczos for current-frame upsampling.
    return Lanczos2ApproxSq(dot(biased, biased));
}

FfxFloat32x3 LoadPreparedColor(FfxInt32x2 pixel)
{
    return RGBToYCoCg(ffxMax(FfxFloat32x3(0.0f), LoadInputColor(pixel)) * Exposure());
}

void ComputeUpsampledColorAndWeight(const AccumulationPassCommonParams params,
    inout AccumulationPassData data)
{
    const FfxFloat32x2 dstOutputPos = FfxFloat32x2(params.iPxHrPos) + 0.5f;
    const FfxFloat32x2 srcOutputPos = dstOutputPos * DownscaleFactor();
    const FfxInt32x2 srcInputPos = FfxInt32x2(floor(srcOutputPos));
    const FfxFloat32x2 srcUnjitteredPos = FfxFloat32x2(srcInputPos) + 0.5f - Jitter();
    const FfxFloat32x2 baseSampleOffset = srcUnjitteredPos - srcOutputPos;
    FfxInt32x2 offsetTL;
    offsetTL.x = srcUnjitteredPos.x > srcOutputPos.x ? -2 : -1;
    offsetTL.y = srcUnjitteredPos.y > srcOutputPos.y ? -2 : -1;
    const FfxBoolean flipRow = srcUnjitteredPos.y > srcOutputPos.y;
    const FfxBoolean flipCol = srcUnjitteredPos.x > srcOutputPos.x;
    const FfxBoolean initialSample = params.fAccumulation == 0.0f;
    FfxFloat32x3 samples[9];
    FfxInt32 sampleIndex = 0;
    [unroll]
    for (FfxInt32 row = 0; row < 3; ++row)
    {
        [unroll]
        for (FfxInt32 col = 0; col < 3; ++col)
        {
            FfxInt32x2 sampleColRow = FfxInt32x2(flipCol ? 3 - col : col,
                flipRow ? 3 - row : row);
            FfxInt32x2 samplePos = srcInputPos + offsetTL + sampleColRow;
            samples[sampleIndex] = LoadPreparedColor(
                ClampLoad(samplePos, FfxInt32x2(0, 0), RenderSize()));
            if (initialSample)
                samples[sampleIndex] = RGBToYCoCg(Tonemap(YCoCgToRGB(samples[sampleIndex])));
            ++sampleIndex;
        }
    }

    const FfxFloat32 kernelBiasMax = ffxMin(1.99f,
        1.0f + (1.0f / DownscaleFactor().x - 1.0f));
    const FfxFloat32 kernelBiasMin = ffxMax(1.0f, (1.0f + kernelBiasMax) * 0.3f);
    const FfxFloat32 kernelBiasWeight = ffxMin(1.0f - params.fDisocclusion * 0.5f,
        ffxMin(1.0f - params.fShadingChange, ffxSaturate(data.fHistoryWeight * 5.0f)));
    const FfxFloat32 kernelBias = ffxLerp(kernelBiasMin, kernelBiasMax, kernelBiasWeight);

    sampleIndex = 0;
    [unroll]
    for (FfxInt32 row = 0; row < 3; ++row)
    {
        [unroll]
        for (FfxInt32 col = 0; col < 3; ++col)
        {
            FfxInt32x2 sampleColRow = FfxInt32x2(flipCol ? 3 - col : col,
                flipRow ? 3 - row : row);
            FfxFloat32x2 sampleOffset = baseSampleOffset + FfxFloat32x2(offsetTL + sampleColRow);
            FfxInt32x2 samplePos = srcInputPos + offsetTL + sampleColRow;
            FfxFloat32 onScreen = FfxFloat32(IsOnScreen(samplePos, RenderSize()));
            if (!initialSample)
            {
                FfxFloat32 weight = onScreen * GetUpsampleLanczosWeight(sampleOffset, kernelBias);
                data.fUpsampledColor += samples[sampleIndex] * weight;
                data.fUpsampledWeight += weight;
            }
            FfxFloat32 boxWeight = exp(-2.3f * dot(sampleOffset, sampleOffset)) * onScreen;
            RectificationBoxAddSample(row == 0 && col == 0, data.clippingBox,
                samples[sampleIndex], boxWeight);
            ++sampleIndex;
        }
    }

    RectificationBoxComputeVarianceBoxData(data.clippingBox);
    data.fUpsampledWeight *= FfxFloat32(data.fUpsampledWeight > FSR3UPSCALER_EPSILON);
    if (data.fUpsampledWeight > FSR3UPSCALER_EPSILON)
    {
        data.fUpsampledColor /= data.fUpsampledWeight;
        data.fUpsampledWeight *= fAverageLanczosWeightPerFrame;
        data.fUpsampledColor = clamp(data.fUpsampledColor,
            data.clippingBox.aabbMin, data.clippingBox.aabbMax);
    }
    if (initialSample)
    {
        data.fUpsampledColor = RGBToYCoCg(InverseTonemap(YCoCgToRGB(data.clippingBox.boxCenter)));
        data.fUpsampledWeight = 1.0f;
        data.fHistoryWeight = 0.0f;
    }
}

FfxFloat32x2 GetMotionVector(FfxInt32x2 pixel, FfxFloat32x2 uv)
{
    // Engine permutation: low-resolution motion vectors.
    return LoadDilatedMotionVector(FfxInt32x2(uv * RenderSize()));
}

void ComputeReprojectedUVs(inout AccumulationPassCommonParams params)
{
    params.fReprojectedHrUv = params.fHrUv + params.fMotionVector;
    params.bIsExistingSample = IsUvInside(params.fReprojectedHrUv);
}

void ReprojectHistoryColor(const AccumulationPassCommonParams params, inout AccumulationPassData data)
{
    FfxFloat32x4 history = HistorySample(params.fReprojectedHrUv, PreviousFrameUpscaleSize());
    data.fHistoryColor = RGBToYCoCg(history.rgb * DeltaPreExposure() * Exposure());
    data.fLock = history.w;
}

void UpdateLockStatus(AccumulationPassCommonParams params, inout AccumulationPassData data)
{
    data.fLock *= FfxFloat32(!params.bIsNewSample);
    FfxFloat32 decreaseFactor = ffxMax(ffxSaturate(params.fShadingChange),
        ffxMax(params.fReactiveMask, params.fDisocclusion));
    data.fLock = ffxMax(0.0f, data.fLock - decreaseFactor * fLockMax);
    data.fLockContributionThisFrame = ffxSaturate(ffxSaturate(data.fLock - fLockThreshold) *
        (fLockMax - fLockThreshold));
    FfxFloat32 newLock = LoadRwNewLocks(params.iPxHrPos) *
        (1.0f - ffxMax(params.fShadingChange * 0.0f, params.fReactiveMask));
    data.fLock = ffxMax(0.0f, ffxMin(data.fLock + newLock, fLockMax));
    data.fLock = ffxMax(0.0f, data.fLock - (0.1f / JitterSequenceLength()) *
        (1.0f - decreaseFactor));
    data.fLock *= FfxFloat32(IsUvInside(params.fHrUv - params.fMotionVector));
}

void RectifyHistory(const AccumulationPassCommonParams params, inout AccumulationPassData data)
{
    FfxFloat32 boxScaleT = ffxMax(ffxSaturate(params.f4KVelocity / 20.0f),
        ffxMax(ffxSaturate(0.75f - params.fFarthestDepthInMeters / 20.0f),
        ffxMax(1.0f - params.fAccumulation,
        ffxMax(ffxPow(params.fReactiveMask, 0.5f), params.fShadingChange))));
    FfxFloat32x3 scaledBoxVec = data.clippingBox.boxVec * FfxFloat32x3(1.7f, 1.0f, 1.0f) *
        ffxLerp(3.0f, 1.0f, boxScaleT);
    FfxFloat32x3 transformed = (data.fHistoryColor - data.clippingBox.boxCenter) /
        ffxMax(scaledBoxVec, FfxFloat32x3(1.193e-7f));
    if (length(transformed) > 1.0f)
    {
        FfxFloat32x3 clampedHistory = normalize(transformed) * scaledBoxVec + data.clippingBox.boxCenter;
        FfxFloat32 contribution = ffxMax(params.fLumaInstabilityFactor,
            data.fLockContributionThisFrame) * params.fAccumulation * (1.0f - params.fDisocclusion);
        data.fHistoryColor = ffxLerp(clampedHistory, data.fHistoryColor, ffxSaturate(contribution));
    }
}

void AccumulatePixel(FfxInt32x2 pixel)
{
    if (!IsOnScreen(pixel, UpscaleSize())) return;
    AccumulationPassCommonParams params;
    AccumulationPassData data;
    params.iPxHrPos = pixel;
    params.fHrUv = (pixel + 0.5f) / UpscaleSize();
    params.fLrUvJittered = params.fHrUv + Jitter() / RenderSize();
    params.fLrUv_HwSampler = ClampUv(params.fLrUvJittered, RenderSize(), MaxRenderSize());
    params.fMotionVector = GetMotionVector(pixel, params.fHrUv);
    params.f4KVelocity = Get4KVelocity(params.fMotionVector);
    ComputeReprojectedUVs(params);
    params.fLumaInstabilityFactor = SampleLumaInstability(
        ClampUv(params.fHrUv, RenderSize(), MaxRenderSize()));
    params.fFarthestDepthInMeters = SampleFarthestDepthMip1(
        ClampUv(params.fLrUvJittered, RenderSize() / 2, GetFarthestDepthMip1ResourceDimensions()));
    params.bIsNewSample = !params.bIsExistingSample || FrameIndex() == 0;
    FfxFloat32x4 masks = SampleDilatedReactiveMasks(params.fLrUv_HwSampler);
    params.fReactiveMask = ffxSaturate(masks[REACTIVE]);
    params.fDisocclusion = ffxSaturate(masks[DISOCCLUSION]);
    params.fShadingChange = ffxSaturate(masks[SHADING_CHANGE]);
    params.fAccumulation = ffxSaturate(masks[ACCUMULAION]);
    params.fAccumulation *= FfxFloat32(round(params.fAccumulation * 100.0f) > 1.0f);
    data.fUpsampledColor = 0.0f;
    data.fHistoryColor = 0.0f;
    data.fHistoryWeight = 1.0f;
    data.fUpsampledWeight = 0.0f;
    data.fLock = 0.0f;
    data.fLockContributionThisFrame = 0.0f;
    if (params.bIsExistingSample && !params.bIsNewSample) ReprojectHistoryColor(params, data);
    UpdateLockStatus(params, data);
    data.fHistoryWeight = ffxMin(params.fAccumulation,
        ffxLerp(params.fAccumulation, 0.15f,
        ffxSaturate(ffxMax(0.0f, (params.f4KVelocity * VelocityFactor()) / 0.5f))));
    ComputeUpsampledColorAndWeight(params, data);
    RectifyHistory(params, data);
    data.fHistoryWeight *= FfxFloat32(data.fHistoryWeight > FSR3UPSCALER_FP16_MIN);
    data.fHistoryWeight = ffxMax(FSR3UPSCALER_EPSILON,
        data.fHistoryWeight + data.fUpsampledWeight);
    data.fUpsampledColor = RGBToYCoCg(Tonemap(YCoCgToRGB(data.fUpsampledColor)));
    data.fHistoryColor = RGBToYCoCg(Tonemap(YCoCgToRGB(data.fHistoryColor)));
    FfxFloat32 alpha = ffxSaturate(data.fUpsampledWeight / data.fHistoryWeight);
    data.fHistoryColor = InverseTonemap(YCoCgToRGB(
        ffxLerp(data.fHistoryColor, data.fUpsampledColor, alpha)));
    data.fHistoryColor = ffxMax(data.fHistoryColor / Exposure(), FfxFloat32x3(0.0f));
    StoreInternalColorAndWeight(pixel, FfxFloat32x4(data.fHistoryColor, data.fLock));
    // Engine permutation: sharpening is enabled, so RCAS owns final output.
    StoreNewLocks(pixel, 0.0f);
}

[numthreads(8, 8, 1)]
void Fsr3AccumulateCS(int2 dispatchThreadId : SV_DispatchThreadID)
{
    AccumulatePixel(dispatchThreadId);
}
