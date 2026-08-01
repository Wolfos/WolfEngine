// FSR3 upscaler - prepare inputs pass.
//
// Ported from ffx-fsr3/upstream/ffx_fsr3upscaler_prepare_inputs.h (FidelityFX SDK
// v1.1.2), with helpers from ffx_fsr3upscaler_common.h in Shaders/fsr3_common.slang.
//
// This is the first pass of the effect. Per render-resolution pixel it:
//   - finds the nearest and farthest depth in a 3x3 neighbourhood
//   - dilates the motion vector towards the nearest sample
//   - scatters the nearest depth into the previous frame's location, so the next
//     frame can tell a disocclusion from a surface that simply moved
//   - writes the dilated MV, dilated depth, farthest view depth, and luma that every
//     later pass consumes
//
// Compile-time permutations resolved for this engine:
//   INVERTED_DEPTH             = 0  (non-reversed depth, far = 1)
//   LOW_RESOLUTION_MOTION_VECTORS = 1  (velocity is a render-resolution G-buffer target)
//   JITTERED_MOTION_VECTORS    = 0  (G-buffer MVs are built from unjittered clip)
// Each is noted at the site it affects rather than left as a define, so the resolved
// behaviour is visible when diffing against upstream.

import "common_bindless.slang";
import "ffx_port_common.slang";
import "fsr3_constants.slang";
import "fsr3_common.slang";
import "fsr3_callbacks.slang";
import "fsr3_reconstructed_depth.slang";

struct DepthExtents
{
    FfxFloat32 fNearest;
    FfxInt32x2 fNearestCoord;
    FfxFloat32 fFarthest;
};

DepthExtents FindDepthExtents(FfxInt32x2 iPxPos)
{
    DepthExtents extents;
    const FfxInt32 iSampleCount = 9;
    const FfxInt32x2 iSampleOffsets[9] = {
        FfxInt32x2(+0, +0),
        FfxInt32x2(+1, +0),
        FfxInt32x2(+0, +1),
        FfxInt32x2(+0, -1),
        FfxInt32x2(-1, +0),
        FfxInt32x2(-1, +1),
        FfxInt32x2(+1, +1),
        FfxInt32x2(-1, -1),
        FfxInt32x2(+1, -1),
    };

    // Pull the depth loads out of the comparison loop so the compiler can batch them.
    FfxFloat32 depth[9];
    FfxInt32 iSampleIndex = 0;
    [unroll]
    for (iSampleIndex = 0; iSampleIndex < iSampleCount; ++iSampleIndex)
    {
        FfxInt32x2 iPos = iPxPos + iSampleOffsets[iSampleIndex];
        depth[iSampleIndex] = LoadInputDepth(iPos);
    }

    extents.fNearestCoord = iPxPos;
    extents.fNearest = depth[0];
    extents.fFarthest = depth[0];
    [unroll]
    for (iSampleIndex = 1; iSampleIndex < iSampleCount; ++iSampleIndex)
    {
        const FfxInt32x2 iPos = iPxPos + iSampleOffsets[iSampleIndex];
        if (IsOnScreen(iPos, RenderSize()))
        {
            FfxFloat32 fNdDepth = depth[iSampleIndex];

            // Non-inverted depth: nearest is smallest, farthest is largest. Reverse-Z
            // swaps the comparison and the ffxMax to ffxMin (upstream's
            // FFX_FSR3UPSCALER_OPTION_INVERTED_DEPTH arm).
            if (fNdDepth < extents.fNearest)
            {
                extents.fFarthest = ffxMax(extents.fFarthest, fNdDepth);
                extents.fNearestCoord = iPos;
                extents.fNearest = fNdDepth;
            }
        }
    }

    return extents;
}

FfxFloat32x2 DilateMotionVector(FfxInt32x2 iPxPos, const DepthExtents depthExtents)
{
    // LOW_RESOLUTION_MOTION_VECTORS: the engine's velocity target is render resolution,
    // so the nearest-depth coordinate indexes it directly. With display-resolution
    // motion vectors upstream would map through ComputeHrPosFromLrPos first.
    const FfxInt32x2 iMotionVectorPos = depthExtents.fNearestCoord;

    return LoadInputMotionVector(iMotionVectorPos);
}

void ReconstructPrevDepth(FfxInt32x2 iPxPos, FfxFloat32 fDepth, FfxFloat32x2 fMotionVector)
{
    const FfxFloat32 fNearestDepthInMeters =
        ffxMin(GetViewSpaceDepthInMeters(fDepth), FSR3UPSCALER_FP16_MAX);
    const FfxFloat32 fReconstructedDeptMvThreshold =
        ReconstructedDepthMvPxThreshold(fNearestDepthInMeters);

    // Discard small mvs.
    fMotionVector *= FfxFloat32(Get4KVelocity(fMotionVector) > fReconstructedDeptMvThreshold);

    const FfxFloat32x2 fUv = (iPxPos + FfxFloat32(0.5)) / RenderSize();
    const FfxFloat32x2 fReprojectedUv = fUv + fMotionVector;
    const BilinearSamplingData bilinearInfo = GetBilinearSamplingData(fReprojectedUv, RenderSize());

    // Project current depth into previous frame locations. Push to every pixel with a
    // meaningful contribution, because the reprojection reads back bilinearly.
    for (FfxInt32 iSampleIndex = 0; iSampleIndex < 4; iSampleIndex++)
    {
        const FfxInt32x2 iOffset = bilinearInfo.iOffsets[iSampleIndex];
        const FfxFloat32 fWeight = bilinearInfo.fWeights[iSampleIndex];

        if (fWeight > fReconstructedDepthBilinearWeightThreshold)
        {
            const FfxInt32x2 iStorePos = bilinearInfo.iBasePos + iOffset;
            if (IsOnScreen(iStorePos, RenderSize()))
            {
                StoreReconstructedDepth(iStorePos, fDepth);
            }
        }
    }
}

FfxFloat32 GetCurrentFrameLuma(FfxInt32x2 iPxPos)
{
    // Assumes linear input. The engine's lighting buffer is linear HDR, so no transfer
    // function is applied here.
    const FfxFloat32x3 fRgb = ffxMax(FfxFloat32x3(0, 0, 0), LoadInputColor(iPxPos));
    return RGBToLuma(fRgb);
}

void PrepareInputs(FfxInt32x2 iPxPos)
{
    const DepthExtents depthExtents = FindDepthExtents(iPxPos);
    const FfxFloat32x2 fDilatedMotionVector = DilateMotionVector(iPxPos, depthExtents);

    ReconstructPrevDepth(iPxPos, depthExtents.fNearest, fDilatedMotionVector);

    StoreDilatedMotionVector(iPxPos, fDilatedMotionVector);
    StoreDilatedDepth(iPxPos, depthExtents.fNearest);

    const FfxFloat32 fFarthestDepthInMeters =
        ffxMin(GetViewSpaceDepthInMeters(depthExtents.fFarthest), FSR3UPSCALER_FP16_MAX);
    StoreFarthestDepth(iPxPos, fFarthestDepthInMeters);

    const FfxFloat32 fLuma = GetCurrentFrameLuma(iPxPos);
    StoreCurrentLuma(iPxPos, fLuma);
}

// Dispatch is ceil(renderSize / 8) - see Fsr3PrepareInputsPass.Record.
[numthreads(8, 8, 1)]
void Fsr3PrepareInputsCS(int2 dispatchThreadId : SV_DispatchThreadID)
{
    // Upstream dispatches exactly over render size and lets the loads clamp. The
    // rounding up to 8 means the trailing groups would otherwise write outside the
    // outputs, so they are dropped here.
    if (IsOnScreen(dispatchThreadId, RenderSize()) == false)
    {
        return;
    }

    PrepareInputs(dispatchThreadId);
}
