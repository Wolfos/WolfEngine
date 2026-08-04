// FSR3 upscaler resource-access layer.
//
// This is the engine's replacement for ffx_fsr3upscaler_callbacks_hlsl.h. Upstream's
// version is NOT ported: it binds a fixed descriptor table declared by the FFX
// backend. Instead we reimplement the same function signatures against the engine's
// bindless registry, so ported pass bodies compile unchanged against either.
//
// That makes this file the single integration seam for the whole effect. Passes call
// LoadX/StoreX; only this file knows about descriptor handles, and only this file has
// to change when the render graph moves a resource around.
//
// Handles live in a b0 cbuffer to match the engine's existing pass convention
// (b0 = bindless handles, b1 = shared FSR3 constants, b2 = per-pass constants).
// The handle block grows as passes are ported; each pass writes only the fields it uses.
//
// See ffx-fsr3/upstream/ffx_fsr3upscaler_resources.h for the authoritative list of
// resources the complete effect needs.

import "../../Common/common_bindless.slang";
import "ffx_port_common.slang";
import "fsr3_constants.slang";
import "fsr3_common.slang";

cbuffer Fsr3BindlessHandles : register(b0)
{
    // --- RCAS pass -----------------------------------------------------------
    uint rcasInputHandle;        // SRV: accumulate output, display resolution
    uint upscaledOutputHandle;   // UAV: final upscaled colour, display resolution
    uint exposureHandle;         // SRV: 1x1 exposure, .x = exposure scale
    uint pointSamplerHandle;
    uint linearSamplerHandle;

    // --- prepare_inputs pass -------------------------------------------------
    uint inputColorHandle;             // SRV: jittered scene colour, render resolution
    uint inputDepthHandle;             // SRV: scene depth, render resolution
    uint inputMotionVectorsHandle;     // SRV: G-buffer velocity, render resolution
    uint dilatedMotionVectorsHandle;   // UAV: .xy = dilated MV in UV space
    uint dilatedDepthHandle;           // UAV in prepare_inputs; SRV in prepare_reactivity
    uint farthestDepthHandle;          // UAV: .x = farthest view depth in metres
    uint currentLumaHandle;            // UAV: .x = linear luma
    uint reconstructedPrevNearestDepthHandle; // UAV, R32Uint: atomic scatter target

    // --- luma_pyramid pass ---------------------------------------------------
    uint currentLumaReadHandle;        // SRV: prepare_inputs' luma output
    uint farthestDepthReadHandle;      // SRV: prepare_inputs' farthest depth output
    uint farthestDepthMip1Handle;      // UAV: half-res farthest depth
    uint frameInfoHandle;              // UAV, 1x1: exposure / log luma / scene average luma
    uint spdGlobalAtomicHandle;        // UAV, R32Uint, coherent: SPD workgroup counter
    uint spdMip0Handle;                // UAV: .xy = log luma, luma
    uint spdMip1Handle;
    uint spdMip2Handle;
    uint spdMip3Handle;
    uint spdMip4Handle;
    uint spdMip5Handle;                // read back by the last SPD workgroup, so coherent

    // --- shading_change_pyramid / shading_change passes -----------------------
    uint previousLumaHandle;           // SRV: last frame's currentLuma
    uint dilatedMotionVectorsReadHandle; // SRV: prepare_inputs' dilated MVs
    uint spdMipReadHandles[6];         // SRV: the six pyramid levels, sampled bilinearly
    uint shadingChangeHandle;          // UAV in shading_change; SRV in prepare_reactivity

    // --- prepare_reactivity pass ---------------------------------------------
    uint reactiveMaskHandle;                 // SRV: render-resolution reactive mask
    uint transparencyAndCompositionMaskHandle; // SRV: render-resolution transparency mask
    uint accumulationReadHandle;             // SRV: persistent accumulation history
    uint accumulationWriteHandle;            // UAV: same persistent accumulation texture
    uint dilatedReactiveMasksHandle;          // UAV: packed RGBA reactivity data
    uint newLocksHandle;                      // UAV: display-resolution thin-feature locks

    // --- debug_view / accumulate history -------------------------------------
    uint internalUpscaledColorHandle;         // SRV: display-resolution color + lock history
    uint internalUpscaledColorWriteHandle;    // UAV: next display-resolution color + lock history

    // --- luma_instability pass -----------------------------------------------
    uint lumaHistoryReadHandle;
    uint lumaHistoryWriteHandle;
    uint lumaInstabilityHandle;

    // --- Passes yet to be ported ---------------------------------------------
    // Add handles here as each pass lands (luma_instability, accumulate, debug_view).
    // Keep this block in sync with Fsr3BindlessHandles on the C# side.
};

// The reconstructed previous-frame nearest depth accessors live in
// fsr3_reconstructed_depth.slang: they need the uint view of the bindless UAV heap, and
// only two passes touch them.

// ---------------------------------------------------------------------------
// Bounds-safe pixel access
//
// Upstream relies on its descriptor table plus clamp-to-edge static samplers. The
// engine's bindless textures carry whatever sampler the pass binds, so loads that
// upstream leaves unguarded are clamped explicitly here.
// ---------------------------------------------------------------------------

// ClampLoad(sample, offset, size) lives in fsr3_common.slang, matching upstream's
// signature. These two are engine-side conveniences over it.

FfxInt32x2 ClampToUpscaleSize(FfxInt32x2 pixel)
{
    return ClampLoad(pixel, FfxInt32x2(0, 0), UpscaleSize());
}

FfxInt32x2 ClampToRenderSize(FfxInt32x2 pixel)
{
    return ClampLoad(pixel, FfxInt32x2(0, 0), RenderSize());
}

// ---------------------------------------------------------------------------
// Exposure
//
// Upstream reads a 1x1 exposure texture written by the luma pyramid pass, with a
// guard so a zeroed or uninitialised resource degrades to a no-op rather than
// blacking out the frame. Until the luma pyramid lands, the C# side binds a 1x1
// texture holding the tonemapper's exposure, which keeps this path exercised.
// ---------------------------------------------------------------------------

FfxFloat32 Exposure()
{
    Texture2D<float4> exposureTexture = GetTexture(exposureHandle);
    FfxFloat32 exposure = exposureTexture.Load(FfxInt32x3(0, 0, 0)).x;

    if (exposure == 0.0f)
    {
        exposure = 1.0f;
    }

    return exposure;
}

// ---------------------------------------------------------------------------
// RCAS
// ---------------------------------------------------------------------------

FfxFloat32x4 LoadRCAS_Input(FfxInt32x2 pixel)
{
    Texture2D<float4> rcasInput = GetTexture(rcasInputHandle);
    return rcasInput.Load(FfxInt32x3(ClampToUpscaleSize(pixel), 0));
}

void StoreUpscaledOutput(FfxInt32x2 pixel, FfxFloat32x3 colour)
{
    RWTexture2D<float4> upscaledOutput = GetRWTexture(upscaledOutputHandle);
    upscaledOutput[pixel] = FfxFloat32x4(colour, 1.0f);
}

// ---------------------------------------------------------------------------
// prepare_inputs inputs
// ---------------------------------------------------------------------------

FfxFloat32 LoadInputDepth(FfxInt32x2 pixel)
{
    Texture2D<float4> inputDepth = GetTexture(inputDepthHandle);
    return inputDepth.Load(FfxInt32x3(ClampToRenderSize(pixel), 0)).x;
}

FfxFloat32x3 LoadInputColor(FfxInt32x2 pixel)
{
    Texture2D<float4> inputColor = GetTexture(inputColorHandle);
    return inputColor.Load(FfxInt32x3(ClampToRenderSize(pixel), 0)).rgb;
}

/// Returns the motion vector in UV space, pointing from this frame to the previous one.
///
/// The engine's G-buffer writes velocity in render-resolution pixels, already computed
/// from unjittered clip positions, so MotionVectorScale carries the 1/renderSize
/// conversion and the jitter cancellation term stays zero. Upstream subtracts
/// MotionVectorJitterCancellation here only when the engine bakes jitter into its
/// motion vectors, which this one does not.
FfxFloat32x2 LoadInputMotionVector(FfxInt32x2 pixel)
{
    Texture2D<float4> inputMotionVectors = GetTexture(inputMotionVectorsHandle);
    FfxFloat32x2 fSrcMotionVector = inputMotionVectors.Load(
        FfxInt32x3(ClampToRenderSize(pixel), 0)).xy;

    FfxFloat32x2 fUvMotionVector = fSrcMotionVector * MotionVectorScale();
    fUvMotionVector -= MotionVectorJitterCancellation();

    return fUvMotionVector;
}

// ---------------------------------------------------------------------------
// prepare_inputs outputs
// ---------------------------------------------------------------------------

void StoreDilatedMotionVector(FfxInt32x2 pixel, FfxFloat32x2 fMotionVector)
{
    RWTexture2D<float4> dilatedMotionVectors = GetRWTexture(dilatedMotionVectorsHandle);
    dilatedMotionVectors[pixel] = FfxFloat32x4(fMotionVector, 0.0f, 0.0f);
}

void StoreDilatedDepth(FfxInt32x2 pixel, FfxFloat32 fDepth)
{
    RWTexture2D<float4> dilatedDepth = GetRWTexture(dilatedDepthHandle);
    dilatedDepth[pixel] = FfxFloat32x4(fDepth, 0.0f, 0.0f, 0.0f);
}

void StoreFarthestDepth(FfxInt32x2 pixel, FfxFloat32 fDepth)
{
    RWTexture2D<float4> farthestDepth = GetRWTexture(farthestDepthHandle);
    farthestDepth[pixel] = FfxFloat32x4(fDepth, 0.0f, 0.0f, 0.0f);
}

void StoreCurrentLuma(FfxInt32x2 pixel, FfxFloat32 fLuma)
{
    RWTexture2D<float4> currentLuma = GetRWTexture(currentLumaHandle);
    currentLuma[pixel] = FfxFloat32x4(fLuma, 0.0f, 0.0f, 0.0f);
}

// StoreReconstructedDepth lives in fsr3_reconstructed_depth.slang, alongside the uint
// view it writes through.

// ---------------------------------------------------------------------------
// luma_pyramid inputs and outputs
// ---------------------------------------------------------------------------

FfxFloat32 LoadCurrentLuma(FfxInt32x2 pixel)
{
    Texture2D<float4> currentLuma = GetTexture(currentLumaReadHandle);
    return currentLuma.Load(FfxInt32x3(ClampToRenderSize(pixel), 0)).x;
}

FfxFloat32 LoadFarthestDepth(FfxInt32x2 pixel)
{
    Texture2D<float4> farthestDepth = GetTexture(farthestDepthReadHandle);
    return farthestDepth.Load(FfxInt32x3(ClampToRenderSize(pixel), 0)).x;
}

void StoreFarthestDepthMip1(FfxInt32x2 pixel, FfxFloat32 fDepth)
{
    RWTexture2D<float4> farthestDepthMip1 = GetRWTexture(farthestDepthMip1Handle);
    farthestDepthMip1[pixel] = FfxFloat32x4(fDepth, 0.0f, 0.0f, 0.0f);
}

/// Frame-wide scalars produced at the 1x1 level of the luma pyramid: see the
/// FRAME_INFO_* indices in fsr3_common.slang.
FfxFloat32x4 LoadFrameInfo()
{
    RWTexture2D<float4> frameInfo = GetRWTexture(frameInfoHandle);
    return frameInfo[FfxInt32x2(0, 0)];
}

void StoreFrameInfo(FfxFloat32x4 fInfo)
{
    RWTexture2D<float4> frameInfo = GetRWTexture(frameInfoHandle);
    frameInfo[FfxInt32x2(0, 0)] = fInfo;
}

/// Resolves a mip index to its bindless handle. Upstream declares six separate UAVs and
/// selects with a macro-generated if-chain; the handles differ here but the shape is the
/// same, because a mip index is not dynamically indexable into distinct resources.
uint Fsr3SpdMipHandle(FfxUInt32 index)
{
    if (index == 0) return spdMip0Handle;
    if (index == 1) return spdMip1Handle;
    if (index == 2) return spdMip2Handle;
    if (index == 3) return spdMip3Handle;
    if (index == 4) return spdMip4Handle;
    return spdMip5Handle;
}

/// Mip 5 is written by every thread group and read back by the elected last one, so it
/// must go through the coherent view. The other mips are only ever touched by the group
/// that wrote them.
FfxFloat32x2 RWLoadPyramid(FfxInt32x2 pixel, FfxUInt32 index)
{
    if (index == 5)
    {
        return LoadRWTextureCoherent(spdMip5Handle, pixel).xy;
    }

    return GetRWTexture(Fsr3SpdMipHandle(index))[pixel].xy;
}

void StorePyramid(FfxInt32x2 pixel, FfxFloat32x2 outValue, FfxUInt32 index)
{
    if (index == 5)
    {
        StoreRWTextureCoherent(spdMip5Handle, pixel, FfxFloat32x4(outValue, 0.0f, 0.0f));
        return;
    }

    GetRWTexture(Fsr3SpdMipHandle(index))[pixel] = FfxFloat32x4(outValue, 0.0f, 0.0f);
}

void SPD_IncreaseAtomicCounter(inout FfxUInt32 spdCounter)
{
    RWTexture2D<uint> globalAtomic = GetRWTextureUint(spdGlobalAtomicHandle);
    InterlockedAdd(globalAtomic[FfxInt32x2(0, 0)], 1u, spdCounter);
}

void SPD_ResetAtomicCounter()
{
    RWTexture2D<uint> globalAtomic = GetRWTextureUint(spdGlobalAtomicHandle);
    globalAtomic[FfxInt32x2(0, 0)] = 0u;
}

// ---------------------------------------------------------------------------
// shading_change inputs and outputs
// ---------------------------------------------------------------------------

FfxFloat32 LoadPreviousLuma(FfxInt32x2 pixel)
{
    Texture2D<float4> previousLuma = GetTexture(previousLumaHandle);
    return previousLuma.Load(FfxInt32x3(ClampLoad(pixel, FfxInt32x2(0, 0), PreviousFrameRenderSize()), 0)).x;
}

FfxFloat32x2 LoadDilatedMotionVector(FfxInt32x2 pixel)
{
    Texture2D<float4> dilatedMotionVectors = GetTexture(dilatedMotionVectorsReadHandle);
    return dilatedMotionVectors.Load(FfxInt32x3(ClampToRenderSize(pixel), 0)).xy;
}

FfxFloat32 LoadDilatedDepth(FfxInt32x2 pixel)
{
    Texture2D<float4> dilatedDepth = GetTexture(dilatedDepthHandle);
    return dilatedDepth.Load(FfxInt32x3(ClampToRenderSize(pixel), 0)).x;
}

FfxFloat32x2 SampleDilatedMotionVector(FfxFloat32x2 fUv)
{
    Texture2D<float4> dilatedMotionVectors = GetTexture(dilatedMotionVectorsReadHandle);
    SamplerState linearSampler = GetSampler(linearSamplerHandle);
    return dilatedMotionVectors.SampleLevel(linearSampler, fUv, 0.0f).xy;
}

FfxFloat32 SampleDilatedDepth(FfxFloat32x2 fUv)
{
    Texture2D<float4> dilatedDepth = GetTexture(dilatedDepthHandle);
    SamplerState linearSampler = GetSampler(linearSamplerHandle);
    return dilatedDepth.SampleLevel(linearSampler, fUv, 0.0f).x;
}

void StoreShadingChange(FfxInt32x2 pixel, FfxFloat32 fShadingChange)
{
    RWTexture2D<float4> shadingChange = GetRWTexture(shadingChangeHandle);
    shadingChange[pixel] = FfxFloat32x4(fShadingChange, 0.0f, 0.0f, 0.0f);
}

/// Dimensions of pyramid level 0 as allocated.
///
/// Upstream queries the mip chain with GetDimensions. This port keeps each level in its
/// own single-mip texture - the engine creates UAVs at mip slice 0 only, so a shared
/// chain would need per-mip views that the bindless registry does not currently produce.
/// The levels are still sized as a chain (level n is level 0 halved n times), so a
/// normalised UV addresses the same texels either way; only the addressing changes, not
/// the values.
FfxInt32x2 GetSPDMipDimensions(FfxUInt32 uMipLevel)
{
    return ffxMax(ShadingChangeMaxRenderSize() >> FfxInt32(uMipLevel), FfxInt32x2(1, 1));
}

/// Bilinear tap into one pyramid level. `fUV` is normalised against level 0's allocated
/// size, exactly as upstream's SampleLevel against the mip chain.
FfxFloat32x2 SampleSPDMipLevel(FfxFloat32x2 fUV, FfxUInt32 mipLevel)
{
    SamplerState linearSampler = GetSampler(linearSamplerHandle);
    Texture2D<float4> mip = GetTexture(spdMipReadHandles[mipLevel]);
    return mip.SampleLevel(linearSampler, fUV, 0.0f).xy;
}


// ---------------------------------------------------------------------------
// prepare_reactivity inputs and outputs
// ---------------------------------------------------------------------------

FfxFloat32 LoadReactiveMask(FfxInt32x2 pixel)
{
    Texture2D<float4> reactiveMask = GetTexture(reactiveMaskHandle);
    // The authored opaque mask is the G-buffer material alpha channel.
    return reactiveMask.Load(FfxInt32x3(ClampToRenderSize(pixel), 0)).a;
}

FfxInt32x2 GetTransparencyAndCompositionMaskResourceDimensions()
{
    // The engine integration allocates the authored transparency mask at render
    // resolution. Keeping this explicit avoids backend-specific GetDimensions lowering
    // in the bindless callback layer.
    return RenderSize();
}

FfxFloat32 SampleTransparencyAndCompositionMask(FfxFloat32x2 fUv)
{
    Texture2D<float4> transparencyMask = GetTexture(transparencyAndCompositionMaskHandle);
    SamplerState linearSampler = GetSampler(linearSamplerHandle);
    return transparencyMask.SampleLevel(linearSampler, fUv, 0.0f).x;
}

FfxFloat32 SampleAccumulation(FfxFloat32x2 fUv)
{
    Texture2D<float4> accumulation = GetTexture(accumulationReadHandle);
    SamplerState linearSampler = GetSampler(linearSamplerHandle);
    return accumulation.SampleLevel(linearSampler, fUv, 0.0f).x;
}

void StoreAccumulation(FfxInt32x2 pixel, FfxFloat32 value)
{
    RWTexture2D<float4> accumulation = GetRWTexture(accumulationWriteHandle);
    accumulation[pixel] = FfxFloat32x4(value, 0.0f, 0.0f, 0.0f);
}

FfxFloat32 SampleShadingChange(FfxFloat32x2 fUv)
{
    Texture2D<float4> shadingChange = GetTexture(shadingChangeHandle);
    SamplerState linearSampler = GetSampler(linearSamplerHandle);
    return shadingChange.SampleLevel(linearSampler, fUv, 0.0f).x;
}

void StoreDilatedReactiveMasks(FfxInt32x2 pixel, FfxFloat32x4 value)
{
    RWTexture2D<float4> dilatedReactiveMasks = GetRWTexture(dilatedReactiveMasksHandle);
    dilatedReactiveMasks[pixel] = value;
}

void StoreNewLocks(FfxInt32x2 pixel, FfxFloat32 value)
{
    RWTexture2D<float4> newLocks = GetRWTexture(newLocksHandle);
    newLocks[pixel] = FfxFloat32x4(value, 0.0f, 0.0f, 0.0f);
}

FfxFloat32x4 SampleDilatedReactiveMasks(FfxFloat32x2 fUv)
{
    Texture2D<float4> masks = GetTexture(dilatedReactiveMasksHandle);
    SamplerState linearSampler = GetSampler(linearSamplerHandle);
    return masks.SampleLevel(linearSampler, fUv, 0.0f);
}

FfxFloat32x4 SampleHistory(FfxFloat32x2 fUv)
{
    Texture2D<float4> history = GetTexture(internalUpscaledColorHandle);
    SamplerState linearSampler = GetSampler(linearSamplerHandle);
    return history.SampleLevel(linearSampler, fUv, 0.0f);
}

FfxFloat32 SampleCurrentLuma(FfxFloat32x2 fUv)
{
    Texture2D<float4> currentLuma = GetTexture(currentLumaReadHandle);
    SamplerState linearSampler = GetSampler(linearSamplerHandle);
    return currentLuma.SampleLevel(linearSampler, fUv, 0.0f).x;
}

FfxFloat32x4 SampleLumaHistory(FfxFloat32x2 fUv)
{
    Texture2D<float4> history = GetTexture(lumaHistoryReadHandle);
    SamplerState linearSampler = GetSampler(linearSamplerHandle);
    return history.SampleLevel(linearSampler, fUv, 0.0f);
}

void StoreLumaHistory(FfxInt32x2 pixel, FfxFloat32x4 value)
{
    GetRWTexture(lumaHistoryWriteHandle)[pixel] = value;
}

FfxInt32x2 GetFarthestDepthMip1ResourceDimensions()
{
    return ffxMax(MaxRenderSize() / 2, FfxInt32x2(1, 1));
}

FfxFloat32 SampleFarthestDepthMip1(FfxFloat32x2 fUv)
{
    Texture2D<float4> depth = GetTexture(farthestDepthMip1Handle);
    SamplerState linearSampler = GetSampler(linearSamplerHandle);
    return depth.SampleLevel(linearSampler, fUv, 0.0f).x;
}

void StoreLumaInstability(FfxInt32x2 pixel, FfxFloat32 value)
{
    GetRWTexture(lumaInstabilityHandle)[pixel] = FfxFloat32x4(value, 0.0f, 0.0f, 0.0f);
}

FfxFloat32 SampleLumaInstability(FfxFloat32x2 fUv)
{
    Texture2D<float4> instability = GetTexture(lumaInstabilityHandle);
    SamplerState linearSampler = GetSampler(linearSamplerHandle);
    return instability.SampleLevel(linearSampler, fUv, 0.0f).x;
}

FfxFloat32x4 LoadHistory(FfxInt32x2 pixel)
{
    Texture2D<float4> history = GetTexture(internalUpscaledColorHandle);
    return history.Load(FfxInt32x3(ClampLoad(pixel, FfxInt32x2(0, 0), PreviousFrameUpscaleSize()), 0));
}

FfxFloat32 LoadRwNewLocks(FfxInt32x2 pixel)
{
    return GetRWTexture(newLocksHandle)[pixel].x;
}

void StoreInternalColorAndWeight(FfxInt32x2 pixel, FfxFloat32x4 value)
{
    GetRWTexture(internalUpscaledColorWriteHandle)[pixel] = value;
}
