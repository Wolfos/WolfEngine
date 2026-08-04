// Minimal replacement for AMD's ffx_core.h, for the FSR3 upscaler port.
//
// Upstream ffx_core.h is ~257 KB of macro layer whose job is to abstract over
// HLSL vs GLSL and fp16 vs fp32. Slang already provides that portability, so
// rather than translating the macro layer we implement only the primitives the
// ported FSR3 passes actually reference, with upstream's exact semantics.
//
// Bit constants and formulations are taken verbatim from FidelityFX SDK v1.1.2
// (ffx_core_gpu_common.h, ffx_core_hlsl.h) so that ported passes stay numerically
// identical to the reference. See Shaders/ffx-fsr3/ for provenance and license.
//
// Everything here is fp32. The fp16 variants are deliberately absent: the port
// targets numerical parity first, and packed-half optimisation only once each
// pass matches its reference output.

#ifndef WOLF_FFX_PORT_COMMON_INCLUDED
#define WOLF_FFX_PORT_COMMON_INCLUDED

// ---------------------------------------------------------------------------
// Type aliases
//
// Kept so that ported pass bodies read the same as upstream and remain diffable
// against a future SDK revision.
// ---------------------------------------------------------------------------

typedef float FfxFloat32;
typedef float2 FfxFloat32x2;
typedef float3 FfxFloat32x3;
typedef float4 FfxFloat32x4;

typedef int FfxInt32;
typedef int2 FfxInt32x2;
typedef int3 FfxInt32x3;
typedef int4 FfxInt32x4;

typedef uint FfxUInt32;
typedef uint2 FfxUInt32x2;
typedef uint3 FfxUInt32x3;
typedef uint4 FfxUInt32x4;

typedef bool FfxBoolean;

// ---------------------------------------------------------------------------
// Bit casts
// ---------------------------------------------------------------------------

FfxFloat32 ffxAsFloat(FfxUInt32 value) { return asfloat(value); }
FfxFloat32x2 ffxAsFloat(FfxUInt32x2 value) { return asfloat(value); }
FfxFloat32x3 ffxAsFloat(FfxUInt32x3 value) { return asfloat(value); }
FfxFloat32x4 ffxAsFloat(FfxUInt32x4 value) { return asfloat(value); }

FfxUInt32 ffxAsUInt32(FfxFloat32 value) { return asuint(value); }
FfxUInt32x2 ffxAsUInt32(FfxFloat32x2 value) { return asuint(value); }
FfxUInt32x3 ffxAsUInt32(FfxFloat32x3 value) { return asuint(value); }
FfxUInt32x4 ffxAsUInt32(FfxFloat32x4 value) { return asuint(value); }

// ---------------------------------------------------------------------------
// Min / max / saturate
//
// Upstream defines ffxMin/ffxMax as thin wrappers so that platform-specific
// medium-precision variants can be swapped in. We keep the names for diffability.
// ---------------------------------------------------------------------------

FfxFloat32 ffxMin(FfxFloat32 x, FfxFloat32 y) { return min(x, y); }
FfxFloat32x2 ffxMin(FfxFloat32x2 x, FfxFloat32x2 y) { return min(x, y); }
FfxFloat32x3 ffxMin(FfxFloat32x3 x, FfxFloat32x3 y) { return min(x, y); }
FfxFloat32x4 ffxMin(FfxFloat32x4 x, FfxFloat32x4 y) { return min(x, y); }
FfxInt32 ffxMin(FfxInt32 x, FfxInt32 y) { return min(x, y); }
FfxInt32x2 ffxMin(FfxInt32x2 x, FfxInt32x2 y) { return min(x, y); }
FfxUInt32 ffxMin(FfxUInt32 x, FfxUInt32 y) { return min(x, y); }
FfxUInt32x2 ffxMin(FfxUInt32x2 x, FfxUInt32x2 y) { return min(x, y); }

FfxFloat32 ffxMax(FfxFloat32 x, FfxFloat32 y) { return max(x, y); }
FfxFloat32x2 ffxMax(FfxFloat32x2 x, FfxFloat32x2 y) { return max(x, y); }
FfxFloat32x3 ffxMax(FfxFloat32x3 x, FfxFloat32x3 y) { return max(x, y); }
FfxFloat32x4 ffxMax(FfxFloat32x4 x, FfxFloat32x4 y) { return max(x, y); }
FfxInt32 ffxMax(FfxInt32 x, FfxInt32 y) { return max(x, y); }
FfxInt32x2 ffxMax(FfxInt32x2 x, FfxInt32x2 y) { return max(x, y); }
FfxUInt32 ffxMax(FfxUInt32 x, FfxUInt32 y) { return max(x, y); }
FfxUInt32x2 ffxMax(FfxUInt32x2 x, FfxUInt32x2 y) { return max(x, y); }

FfxFloat32 ffxMax3(FfxFloat32 x, FfxFloat32 y, FfxFloat32 z) { return max(x, max(y, z)); }
FfxFloat32x2 ffxMax3(FfxFloat32x2 x, FfxFloat32x2 y, FfxFloat32x2 z) { return max(x, max(y, z)); }
FfxFloat32x3 ffxMax3(FfxFloat32x3 x, FfxFloat32x3 y, FfxFloat32x3 z) { return max(x, max(y, z)); }

FfxFloat32 ffxMin3(FfxFloat32 x, FfxFloat32 y, FfxFloat32 z) { return min(x, min(y, z)); }
FfxFloat32x2 ffxMin3(FfxFloat32x2 x, FfxFloat32x2 y, FfxFloat32x2 z) { return min(x, min(y, z)); }
FfxFloat32x3 ffxMin3(FfxFloat32x3 x, FfxFloat32x3 y, FfxFloat32x3 z) { return min(x, min(y, z)); }

FfxFloat32 ffxSaturate(FfxFloat32 value) { return saturate(value); }
FfxFloat32x2 ffxSaturate(FfxFloat32x2 value) { return saturate(value); }
FfxFloat32x3 ffxSaturate(FfxFloat32x3 value) { return saturate(value); }
FfxFloat32x4 ffxSaturate(FfxFloat32x4 value) { return saturate(value); }

FfxFloat32 ffxLerp(FfxFloat32 x, FfxFloat32 y, FfxFloat32 t) { return lerp(x, y, t); }
FfxFloat32x2 ffxLerp(FfxFloat32x2 x, FfxFloat32x2 y, FfxFloat32 t) { return lerp(x, y, t); }
FfxFloat32x3 ffxLerp(FfxFloat32x3 x, FfxFloat32x3 y, FfxFloat32 t) { return lerp(x, y, t); }
FfxFloat32x3 ffxLerp(FfxFloat32x3 x, FfxFloat32x3 y, FfxFloat32x3 t) { return lerp(x, y, t); }
FfxFloat32x4 ffxLerp(FfxFloat32x4 x, FfxFloat32x4 y, FfxFloat32 t) { return lerp(x, y, t); }

FfxFloat32 ffxPow(FfxFloat32 x, FfxFloat32 y) { return pow(x, y); }
FfxFloat32 ffxSqrt(FfxFloat32 x) { return sqrt(x); }

FfxFloat32 ffxFract(FfxFloat32 x) { return frac(x); }
FfxFloat32x2 ffxFract(FfxFloat32x2 x) { return frac(x); }
FfxFloat32x3 ffxFract(FfxFloat32x3 x) { return frac(x); }

// ---------------------------------------------------------------------------
// Reciprocals
//
// The approximate variants are not an optimisation we are free to skip: RCAS
// depends on the exact error characteristics of ffxApproximateReciprocalMedium
// to avoid visible tonality shifts. Constants are upstream's.
// ---------------------------------------------------------------------------

FfxFloat32 ffxReciprocal(FfxFloat32 x) { return rcp(x); }
FfxFloat32x2 ffxReciprocal(FfxFloat32x2 x) { return rcp(x); }
FfxFloat32x3 ffxReciprocal(FfxFloat32x3 x) { return rcp(x); }

FfxFloat32 ffxApproximateReciprocal(FfxFloat32 value)
{
    return ffxAsFloat(FfxUInt32(0x7ef07ebbu) - ffxAsUInt32(value));
}

FfxFloat32 ffxApproximateReciprocalMedium(FfxFloat32 value)
{
    FfxFloat32 b = ffxAsFloat(FfxUInt32(0x7ef19fffu) - ffxAsUInt32(value));
    return b * (-b * value + FfxFloat32(2.0));
}

FfxFloat32 ffxApproximateSqrt(FfxFloat32 value)
{
    return ffxAsFloat((ffxAsUInt32(value) >> FfxUInt32(1)) + FfxUInt32(0x1fbc4639u));
}

// ---------------------------------------------------------------------------
// Bitfield helpers
// ---------------------------------------------------------------------------

FfxUInt32 ffxBitfieldExtract(FfxUInt32 src, FfxUInt32 off, FfxUInt32 bits)
{
    FfxUInt32 mask = (1u << bits) - 1;
    return (src >> off) & mask;
}

FfxUInt32 ffxBitfieldInsert(FfxUInt32 src, FfxUInt32 ins, FfxUInt32 mask)
{
    return (ins & mask) | (src & (~mask));
}

FfxUInt32 ffxBitfieldInsertMask(FfxUInt32 src, FfxUInt32 ins, FfxUInt32 bits)
{
    FfxUInt32 mask = (1u << bits) - 1;
    return (ins & mask) | (src & (~mask));
}

// ---------------------------------------------------------------------------
// Thread remapping
//
// Remaps a linear lane index into a quad-swizzled 2D position, which gives the
// 2x2 texture-fetch locality a pixel shader would have had. FSR3's RCAS and
// accumulate passes both dispatch through this.
// ---------------------------------------------------------------------------

FfxUInt32x2 ffxRemapForQuad(FfxUInt32 a)
{
    return FfxUInt32x2(
        ffxBitfieldExtract(a, 1u, 3u),
        ffxBitfieldInsertMask(ffxBitfieldExtract(a, 3u, 3u), a, 1u));
}

/// Remaps a linear lane index into the 8x8 layout single-pass downsample expects for its
/// 2D wave reductions.
FfxUInt32x2 ffxRemapForWaveReduction(FfxUInt32 a)
{
    return FfxUInt32x2(
        ffxBitfieldInsertMask(ffxBitfieldExtract(a, 2u, 3u), a, 1u),
        ffxBitfieldInsertMask(ffxBitfieldExtract(a, 3u, 3u), ffxBitfieldExtract(a, 1u, 2u), 2u));
}

#endif // WOLF_FFX_PORT_COMMON_INCLUDED
