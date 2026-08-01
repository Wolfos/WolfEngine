// FSR3 upscaler - reconstructed previous-frame nearest depth.
//
// Ported from the FSR3UPSCALER_BIND_UAV_RECONSTRUCTED_PREV_NEAREST_DEPTH accessors in
// ffx-fsr3/upstream/ffx_fsr3upscaler_callbacks_hlsl.h.
//
// The target is a TextureFormat.R32Uint image reached through GetRWTextureUint, which
// views the same bindless descriptor as GetRWTexture with a uint element type. Integer
// atomics need that; a float-typed UAV cannot express them.
//
// The stored value is a float device depth bit-cast to uint. For non-negative floats
// the integer ordering matches the float ordering, which is why an integer atomic can
// carry a depth reduction at all.

#ifndef WOLF_FSR3_RECONSTRUCTED_DEPTH_INCLUDED
#define WOLF_FSR3_RECONSTRUCTED_DEPTH_INCLUDED

import "common_bindless.slang";
import "ffx_port_common.slang";
import "fsr3_constants.slang";
import "fsr3_callbacks.slang";

/// Scatters a device depth into the previous frame's reconstruction target.
///
/// The engine uses non-inverted depth (far = 1), so this takes upstream's
/// FFX_FSR3UPSCALER_OPTION_INVERTED_DEPTH == 0 arm: nearest is smallest, hence
/// InterlockedMin. Moving the engine to reverse-Z means changing this to
/// InterlockedMax, the clear value in Fsr3PrepareInputsPass, and the two comparisons in
/// FindDepthExtents - nothing else.
void StoreReconstructedDepth(FfxInt32x2 iPxSample, FfxFloat32 fDepth)
{
    RWTexture2D<uint> reconstructedDepth = GetRWTextureUint(reconstructedPrevNearestDepthHandle);
    uint uDepth = asuint(fDepth);
    InterlockedMin(reconstructedDepth[iPxSample], uDepth);
}

/// Resets the scatter target. Written as a separate entry point rather than a clear so
/// the value matches the depth convention: the atomic below reduces towards the nearest
/// surface, so it must start at the farthest representable depth.
void SetReconstructedDepth(FfxInt32x2 iPxSample, uint uValue)
{
    RWTexture2D<uint> reconstructedDepth = GetRWTextureUint(reconstructedPrevNearestDepthHandle);
    reconstructedDepth[iPxSample] = uValue;
}

/// Reads the atomically reconstructed device depth without converting its R32Uint payload.
/// The engine has one float-typed bindless SRV view, so this pass deliberately keeps the
/// resource in the uint UAV view and bit-casts the stored payload, matching upstream's
/// Texture2D&lt;uint&gt; accessor exactly.
FfxFloat32 LoadReconstructedPrevDepth(FfxInt32x2 iPxSample)
{
    RWTexture2D<uint> reconstructedDepth = GetRWTextureUint(reconstructedPrevNearestDepthHandle);
    return asfloat(reconstructedDepth[ClampToRenderSize(iPxSample)]);
}

#endif // WOLF_FSR3_RECONSTRUCTED_DEPTH_INCLUDED
