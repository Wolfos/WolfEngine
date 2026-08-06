// FSR3 upscaler - prepare reactivity pass.
//
// Ported from ffx-fsr3/upstream/ffx_fsr3upscaler_prepare_reactivity.h
// (FidelityFX SDK v1.1.2), with the improved disocclusion accumulation defaults from
// SDK v1.1.4 / FSR 3.1.4.
//
// Combines authored reactive/transparency masks with disocclusion, motion divergence,
// shading change, and thin-feature detection. The packed output controls how much history
// accumulate may trust at each render-resolution pixel; new locks are written at display
// resolution for thin features that would otherwise disappear under jitter.

import "../../Common/common_bindless.slang";
import "ffx_port_common.slang";
import "fsr3_constants.slang";
import "fsr3_common.slang";
import "fsr3_callbacks.slang";
import "fsr3_reconstructed_depth.slang";

cbuffer cbPrepareReactivity : register(b2)
{
    float alphaTestReactiveScale;
    float transparencyAndCompositionMaskScale;
};

FfxFloat32 ComputeDisocclusions(FfxFloat32x2 fUv, FfxFloat32x2 fMotionVector, FfxFloat32 fCurrentDepthViewSpace)
{
    const FfxFloat32 fNearestDepthInMeters = ffxMin(
        fCurrentDepthViewSpace * ViewSpaceToMetersFactor(), FSR3UPSCALER_FP16_MAX);
    const FfxFloat32 fReconstructedDeptMvThreshold = ReconstructedDepthMvPxThreshold(fNearestDepthInMeters);

    fMotionVector *= FfxFloat32(Get4KVelocity(fMotionVector) > fReconstructedDeptMvThreshold);

    const FfxFloat32x2 fReprojectedUv = fUv + fMotionVector;
    const BilinearSamplingData bilinearInfo = GetBilinearSamplingData(fReprojectedUv, RenderSize());

    FfxFloat32 fDisocclusion = 0.0f;
    FfxFloat32 fWeightSum = 0.0f;
    FfxBoolean bPotentialDisocclusion = true;

    for (FfxInt32 iSampleIndex = 0; iSampleIndex < 4 && bPotentialDisocclusion; iSampleIndex++)
    {
        const FfxInt32x2 iOffset = bilinearInfo.iOffsets[iSampleIndex];
        const FfxInt32x2 iSamplePos = ClampLoad(bilinearInfo.iBasePos, iOffset, RenderSize());

        if (IsOnScreen(iSamplePos, RenderSize()))
        {
            const FfxFloat32 fWeight = bilinearInfo.fWeights[iSampleIndex];
            if (fWeight > fReconstructedDepthBilinearWeightThreshold)
            {
                const FfxFloat32 fPrevNearestDepthViewSpace = GetViewSpaceDepth(
                    LoadReconstructedPrevDepth(iSamplePos));
                const FfxFloat32 fDepthDifference = fCurrentDepthViewSpace - fPrevNearestDepthViewSpace;

                bPotentialDisocclusion = bPotentialDisocclusion &&
                    (fDepthDifference > FSR3UPSCALER_FP32_MIN);

                if (bPotentialDisocclusion)
                {
                    const FfxFloat32 fHalfViewportWidth = length(FfxFloat32x2(RenderSize()) * 0.5f);
                    const FfxFloat32 fDepthThreshold = ffxMax(
                        fCurrentDepthViewSpace, fPrevNearestDepthViewSpace);

                    const FfxFloat32 Ksep = 1.37e-05f;
                    const FfxFloat32 fRequiredDepthSeparation =
                        Ksep * fHalfViewportWidth * fDepthThreshold;

                    fDisocclusion += ffxSaturate(fRequiredDepthSeparation / fDepthDifference) * fWeight;
                    fWeightSum += fWeight;
                }
            }
        }
    }

    fDisocclusion = (bPotentialDisocclusion && fWeightSum > 0.0f)
        ? ffxSaturate(1.0f - fDisocclusion / fWeightSum)
        : 0.0f;

    return fDisocclusion;
}

// The reference four-tap test above requires every contributing previous-depth tap to
// be behind the current surface. At a foreground silhouette, motion-vector dilation
// deliberately selects the foreground while the current raster pixel may already be
// newly exposed background. One matching foreground tap then vetoes the entire test.
// Detect that exact condition from the current and dilated depths so a one-pixel stale
// foreground sample cannot seed a multi-frame colour trail.
FfxFloat32 ComputeDilatedForegroundDisocclusion(
    FfxFloat32 fCurrentDepth,
    FfxFloat32 fDilatedDepth)
{
    const FfxFloat32 fCurrentDepthViewSpace = GetViewSpaceDepth(fCurrentDepth);
    const FfxFloat32 fDilatedDepthViewSpace = GetViewSpaceDepth(fDilatedDepth);
    const FfxFloat32 fDepthDifference =
        fCurrentDepthViewSpace - fDilatedDepthViewSpace;

    const FfxFloat32 fHalfViewportWidth =
        length(FfxFloat32x2(RenderSize()) * 0.5f);
    const FfxFloat32 fDepthThreshold =
        ffxMax(fCurrentDepthViewSpace, fDilatedDepthViewSpace);
    const FfxFloat32 fRequiredDepthSeparation =
        1.37e-05f * fHalfViewportWidth * fDepthThreshold;

    return ffxSaturate(fDepthDifference /
        ffxMax(fRequiredDepthSeparation, FSR3UPSCALER_FP32_MIN));
}

FfxFloat32 ComputeMotionDivergence(
    FfxFloat32x2 fUv,
    FfxFloat32x2 fMotionVector,
    FfxFloat32 fCurrentDepthSample)
{
    const FfxInt32x2 iPxReprojectedPos = FfxInt32x2((fUv + fMotionVector) * RenderSize());
    const FfxFloat32 fReprojectedDepth = LoadDilatedDepth(iPxReprojectedPos);
    const FfxFloat32x2 fReprojectedMotionVector = LoadDilatedMotionVector(iPxReprojectedPos);

    const FfxFloat32 fReprojectedVelocity = Get4KVelocity(fReprojectedMotionVector);
    const FfxFloat32 f4KVelocity = Get4KVelocity(fMotionVector);

    const FfxFloat32 fNucleusDepthInMeters = GetViewSpaceDepthInMeters(fReprojectedDepth);
    const FfxFloat32 fCurrentDepthInMeters = GetViewSpaceDepthInMeters(fCurrentDepthSample);

    const FfxFloat32 fDistanceFactor = MinDividedByMax(
        fNucleusDepthInMeters, fCurrentDepthInMeters);
    const FfxFloat32 fVelocityFactor = ffxSaturate(f4KVelocity / 10.0f);
    const FfxFloat32 fMotionVectorFieldConfidence =
        (1.0f - ffxSaturate(fReprojectedVelocity / f4KVelocity)) *
        fDistanceFactor * fVelocityFactor;

    return fMotionVectorFieldConfidence;
}

FfxFloat32 DilateReactiveMasks(FfxInt32x2 iPxPos, FfxFloat32x2 fUv)
{
    FfxFloat32 fDilatedReactiveMasks = 0.0f;

    [unroll]
    for (FfxInt32 y = -1; y <= 1; y++)
    {
        [unroll]
        for (FfxInt32 x = -1; x <= 1; x++)
        {
            const FfxInt32x2 sampleCoord = ClampLoad(
                iPxPos, FfxInt32x2(x, y), RenderSize());
            fDilatedReactiveMasks = ffxMax(
                fDilatedReactiveMasks,
                LoadReactiveMask(sampleCoord) * alphaTestReactiveScale);
        }
    }

    return fDilatedReactiveMasks;
}

FfxFloat32 DilateTransparencyAndCompositionMasks(FfxInt32x2 iPxPos, FfxFloat32x2 fUv)
{
    const FfxFloat32x2 fUvTransparencyAndCompositionMask = ClampUv(
        fUv, RenderSize(), GetTransparencyAndCompositionMaskResourceDimensions());
    return SampleTransparencyAndCompositionMask(fUvTransparencyAndCompositionMask) *
        transparencyAndCompositionMaskScale;
}

FfxUInt32 Fsr3SetBit(FfxInt32 bitIndex)
{
    // Replaces upstream's local SETBIT macro; macros do not cross Slang module boundaries.
    return 1u << FfxUInt32(bitIndex);
}

FfxFloat32 ComputeThinFeatureConfidence(FfxInt32x2 iPxPos)
{
    /*
       1 2 3
       4 0 5
       6 7 8
    */

    const FfxInt32 iNucleusIndex = 0;
    const FfxInt32 iSampleCount = 9;
    const FfxInt32x2 iSampleOffsets[iSampleCount] = {
        FfxInt32x2(+0, +0),
        FfxInt32x2(-1, -1),
        FfxInt32x2(+0, -1),
        FfxInt32x2(+1, -1),
        FfxInt32x2(-1, +0),
        FfxInt32x2(+1, +0),
        FfxInt32x2(-1, +1),
        FfxInt32x2(+0, +1),
        FfxInt32x2(+1, +1),
    };

    FfxFloat32 fSamples[iSampleCount];
    FfxFloat32 fLumaMin = FSR3UPSCALER_FP32_MAX;
    FfxFloat32 fLumaMax = FSR3UPSCALER_FP32_MIN;

    [unroll]
    for (FfxInt32 iSampleIndex = 0; iSampleIndex < iSampleCount; ++iSampleIndex)
    {
        const FfxInt32x2 iPxSamplePos = ClampLoad(
            iPxPos, iSampleOffsets[iSampleIndex], RenderSize());
        fSamples[iSampleIndex] = LoadCurrentLuma(iPxSamplePos) * Exposure();

        fLumaMin = ffxMin(fLumaMin, fSamples[iSampleIndex]);
        fLumaMax = ffxMax(fLumaMax, fSamples[iSampleIndex]);
    }

    const FfxFloat32 fThreshold = 0.9f;
    FfxFloat32 fDissimilarLumaMin = FSR3UPSCALER_FP32_MAX;
    FfxFloat32 fDissimilarLumaMax = 0.0f;

    FfxUInt32 uPatternMask = Fsr3SetBit(iNucleusIndex);

    const FfxUInt32 uNumRejectionMasks = 4;
    const FfxUInt32 uRejectionMasks[uNumRejectionMasks] = {
        Fsr3SetBit(1) | Fsr3SetBit(2) | Fsr3SetBit(4) | Fsr3SetBit(iNucleusIndex),
        Fsr3SetBit(2) | Fsr3SetBit(3) | Fsr3SetBit(5) | Fsr3SetBit(iNucleusIndex),
        Fsr3SetBit(4) | Fsr3SetBit(6) | Fsr3SetBit(7) | Fsr3SetBit(iNucleusIndex),
        Fsr3SetBit(5) | Fsr3SetBit(7) | Fsr3SetBit(8) | Fsr3SetBit(iNucleusIndex)
    };

    FfxInt32 iBitIndex = 1;
    [unroll]
    for (FfxInt32 iSampleIndex = 1; iSampleIndex < iSampleCount; ++iSampleIndex, ++iBitIndex)
    {
        const FfxFloat32 fDifference =
            abs(fSamples[iSampleIndex] - fSamples[iNucleusIndex]) / (fLumaMax - fLumaMin);

        if (fDifference < fThreshold)
        {
            uPatternMask |= Fsr3SetBit(iBitIndex);
        }
        else
        {
            fDissimilarLumaMin = ffxMin(fDissimilarLumaMin, fSamples[iSampleIndex]);
            fDissimilarLumaMax = ffxMax(fDissimilarLumaMax, fSamples[iSampleIndex]);
        }
    }

    const FfxBoolean bIsRidge =
        fSamples[iNucleusIndex] > fDissimilarLumaMax ||
        fSamples[iNucleusIndex] < fDissimilarLumaMin;

    if (bIsRidge == false)
    {
        return 0.0f;
    }

    [unroll]
    for (FfxInt32 i = 0; i < FfxInt32(uNumRejectionMasks); i++)
    {
        if ((uPatternMask & uRejectionMasks[i]) == uRejectionMasks[i])
        {
            return 0.0f;
        }
    }

    return 1.0f - fLumaMin / fLumaMax;
}

FfxFloat32 UpdateAccumulation(
    FfxInt32x2 iPxPos,
    FfxFloat32x2 fUv,
    FfxFloat32x2 fMotionVector,
    FfxFloat32 fDisocclusion,
    FfxFloat32 fShadingChange)
{
    const FfxFloat32x2 fReprojectedUv = fUv + fMotionVector;
    FfxFloat32 fAccumulation = 0.0f;

    if (IsUvInside(fReprojectedUv))
    {
        const FfxFloat32x2 fReprojectedUvHw = ClampUv(
            fReprojectedUv, PreviousFrameRenderSize(), MaxRenderSize());
        fAccumulation = ffxSaturate(SampleAccumulation(fReprojectedUvHw));
    }

    fAccumulation = ffxLerp(fAccumulation, 0.0f, fShadingChange);
    fAccumulation = ffxLerp(
        fAccumulation,
        ffxMin(fMinDisocclusionAccumulation, fAccumulation),
        fDisocclusion);
    fAccumulation *= FfxFloat32(round(fAccumulation * 100.0f) > 1.0f);

    // Update for next frame, normalized to store in unorm. A full disocclusion resolves to
    // zero above, so the stored value begins at one accumulation step and history is not
    // trusted again until the newly revealed surface has contributed a complete frame.
    const FfxFloat32 fAccumulatedFramesToStore =
        ffxSaturate(fAccumulation + fAccumulationAddedPerFrame);
    StoreAccumulation(iPxPos, fAccumulatedFramesToStore);

    return fAccumulation;
}

FfxFloat32 ComputeShadingChange(FfxFloat32x2 fUv)
{
    // Re-apply jitter here; accumulate reverts it again when sampling this output.
    const FfxFloat32x2 fShadingChangeUv = ClampUv(
        fUv - Jitter() / RenderSize(),
        ShadingChangeRenderSize(),
        ShadingChangeMaxRenderSize());
    return ffxSaturate(SampleShadingChange(fShadingChangeUv));
}

void PrepareReactivity(FfxInt32x2 iPxPos)
{
    if (IsOnScreen(iPxPos, RenderSize()) == false)
    {
        return;
    }

    const FfxFloat32x2 fUv = (iPxPos + 0.5f) / RenderSize();
    const FfxFloat32x2 fMotionVector = LoadDilatedMotionVector(iPxPos);
    const FfxFloat32 fDilatedDepth = LoadDilatedDepth(iPxPos);
    const FfxFloat32 fInputDepth = LoadInputDepth(iPxPos);

    // Motion-vector dilation deliberately borrows the nearest foreground sample so
    // reprojection stays stable at silhouettes. Do not also borrow that sample's depth
    // for disocclusion: on newly exposed background it makes the reconstructed previous
    // foreground depth appear to match and preserves the foreground colour as a trail.
    // Comparing the real current-pixel depth keeps the robust dilated motion while
    // correctly rejecting history where the foreground has moved away.
    const FfxFloat32 fDisocclusion = ffxMax(
        ComputeDisocclusions(fUv, fMotionVector, GetViewSpaceDepth(fInputDepth)),
        ComputeDilatedForegroundDisocclusion(fInputDepth, fDilatedDepth));
    const FfxFloat32 fShadingChange = ffxMax(
        DilateReactiveMasks(iPxPos, fUv), ComputeShadingChange(fUv));

    const FfxFloat32 fMotionDivergence = ComputeMotionDivergence(
        fUv, fMotionVector, fDilatedDepth);
    const FfxFloat32 fDilatedTransparencyAndComposition =
        DilateTransparencyAndCompositionMasks(iPxPos, fUv);
    const FfxFloat32 fFinalReactiveness = ffxMax(
        fMotionDivergence, fDilatedTransparencyAndComposition);

    const FfxFloat32 fAccumulation = UpdateAccumulation(
        iPxPos, fUv, fMotionVector, fDisocclusion, fShadingChange);

    FfxFloat32x4 fOutput;
    fOutput[REACTIVE] = fFinalReactiveness;
    fOutput[DISOCCLUSION] = fDisocclusion;
    fOutput[SHADING_CHANGE] = fShadingChange;
    fOutput[ACCUMULAION] = fAccumulation;
    StoreDilatedReactiveMasks(iPxPos, fOutput);

    const FfxFloat32 fLockStrength = ComputeThinFeatureConfidence(iPxPos);
    if (fLockStrength > (1.0f / 100.0f))
    {
        StoreNewLocks(ComputeHrPosFromLrPos(iPxPos), fLockStrength);
    }
}

// Dispatch is ceil(RenderSize / 8) - see Fsr3PrepareReactivityPass.Record.
[numthreads(8, 8, 1)]
void Fsr3PrepareReactivityCS(int2 dispatchThreadId : SV_DispatchThreadID)
{
    PrepareReactivity(dispatchThreadId);
}
