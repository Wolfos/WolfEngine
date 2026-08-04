// Single Pass Downsample (SPD), fp32 wave-intrinsics path.
//
// Ported from ffx-fsr3/upstream/ffx_spd.h (FidelityFX SDK v1.1.2).
//
// TEXTUAL INCLUDE, NOT A SLANG MODULE. Include it with #include, never import it.
//
// SPD is written against callbacks the *including* shader defines - SpdLoadSourceImage,
// SpdLoad, SpdStore, SpdReduce4, SpdLoadIntermediate, SpdStoreIntermediate,
// SpdIncreaseAtomicCounter, SpdGetAtomicCounter, SpdResetAtomicCounter - plus the
// groupshared intermediate storage. Slang modules cannot bind a callee that the importer
// supplies later, so this file relies on the preprocessor exactly as upstream does. The
// including shader must define all of the above before the #include.
//
// Only the fp32, wave-operations path is ported. Upstream's FFX_SPD_NO_WAVE_OPERATIONS
// fallback and its packed-fp16 twin are omitted: quad operations are core in SM6.0 (this
// engine compiles cs_6_6) and Slang maps them onto Metal's SIMD-group quad shuffles, so
// the fallback has no target here. If a device ever needs it, the LDS path is in the
// vendored header.
//
// How it works: each thread group reduces a 64x64 tile down to mip 5. A global atomic
// counter then elects the last group still running, which alone continues from mip 6,
// downsampling the up-to-64x64 remainder. That election is why mip 5 and the counter must
// be reached through the globallycoherent view of the UAV heap - see GetRWTextureCoherent.

#ifndef WOLF_FSR3_SPD_INCLUDED
#define WOLF_FSR3_SPD_INCLUDED

void ffxSpdWorkgroupShuffleBarrier()
{
    GroupMemoryBarrierWithGroupSync();
}

/// Increments the global counter and reports whether this group is NOT the last one.
/// Every group but the last returns true and exits.
bool SpdExitWorkgroup(FfxUInt32 numWorkGroups, FfxUInt32 localInvocationIndex, FfxUInt32 slice)
{
    if (localInvocationIndex == 0)
    {
        SpdIncreaseAtomicCounter(slice);
    }

    ffxSpdWorkgroupShuffleBarrier();
    return SpdGetAtomicCounter() != (numWorkGroups - 1);
}

/// Reduces the 2x2 quad this lane belongs to. Requires SM6.0 quad intrinsics.
FfxFloat32x4 SpdReduceQuad(FfxFloat32x4 v)
{
    FfxFloat32x4 v0 = v;
    FfxFloat32x4 v1 = QuadReadAcrossX(v);
    FfxFloat32x4 v2 = QuadReadAcrossY(v);
    FfxFloat32x4 v3 = QuadReadAcrossDiagonal(v);
    return SpdReduce4(v0, v1, v2, v3);
}

FfxFloat32x4 SpdReduceIntermediate(FfxUInt32x2 i0, FfxUInt32x2 i1, FfxUInt32x2 i2, FfxUInt32x2 i3)
{
    FfxFloat32x4 v0 = SpdLoadIntermediate(i0.x, i0.y);
    FfxFloat32x4 v1 = SpdLoadIntermediate(i1.x, i1.y);
    FfxFloat32x4 v2 = SpdLoadIntermediate(i2.x, i2.y);
    FfxFloat32x4 v3 = SpdLoadIntermediate(i3.x, i3.y);
    return SpdReduce4(v0, v1, v2, v3);
}

FfxFloat32x4 SpdReduceLoad4(FfxUInt32x2 i0, FfxUInt32x2 i1, FfxUInt32x2 i2, FfxUInt32x2 i3, FfxUInt32 slice)
{
    FfxFloat32x4 v0 = SpdLoad(FfxInt32x2(i0), slice);
    FfxFloat32x4 v1 = SpdLoad(FfxInt32x2(i1), slice);
    FfxFloat32x4 v2 = SpdLoad(FfxInt32x2(i2), slice);
    FfxFloat32x4 v3 = SpdLoad(FfxInt32x2(i3), slice);
    return SpdReduce4(v0, v1, v2, v3);
}

FfxFloat32x4 SpdReduceLoad4(FfxUInt32x2 base, FfxUInt32 slice)
{
    return SpdReduceLoad4(
        FfxUInt32x2(base + FfxUInt32x2(0, 0)),
        FfxUInt32x2(base + FfxUInt32x2(0, 1)),
        FfxUInt32x2(base + FfxUInt32x2(1, 0)),
        FfxUInt32x2(base + FfxUInt32x2(1, 1)),
        slice);
}

FfxFloat32x4 SpdReduceLoadSourceImage4(FfxUInt32x2 i0, FfxUInt32x2 i1, FfxUInt32x2 i2, FfxUInt32x2 i3, FfxUInt32 slice)
{
    FfxFloat32x4 v0 = SpdLoadSourceImage(FfxInt32x2(i0), slice);
    FfxFloat32x4 v1 = SpdLoadSourceImage(FfxInt32x2(i1), slice);
    FfxFloat32x4 v2 = SpdLoadSourceImage(FfxInt32x2(i2), slice);
    FfxFloat32x4 v3 = SpdLoadSourceImage(FfxInt32x2(i3), slice);
    return SpdReduce4(v0, v1, v2, v3);
}

FfxFloat32x4 SpdReduceLoadSourceImage(FfxUInt32x2 base, FfxUInt32 slice)
{
    // Upstream's SPD_LINEAR_SAMPLER path is omitted: FSR3's luma pyramid does not define
    // it, and a linear tap would change which texels the reduction sees.
    return SpdReduceLoadSourceImage4(
        FfxUInt32x2(base + FfxUInt32x2(0, 0)),
        FfxUInt32x2(base + FfxUInt32x2(0, 1)),
        FfxUInt32x2(base + FfxUInt32x2(1, 0)),
        FfxUInt32x2(base + FfxUInt32x2(1, 1)),
        slice);
}

// ---------------------------------------------------------------------------
// Mip levels
//
// The LDS index arithmetic below is deliberately irregular: it spreads the surviving
// lanes across banks to avoid conflicts. Do not "simplify" it - the layouts are
// documented in the comments upstream carries.
// ---------------------------------------------------------------------------

void SpdDownsampleMips_0_1(
    FfxUInt32 x,
    FfxUInt32 y,
    FfxUInt32x2 workGroupID,
    FfxUInt32 localInvocationIndex,
    FfxUInt32 mip,
    FfxUInt32 slice)
{
    FfxFloat32x4 v[4];

    FfxInt32x2 tex = FfxInt32x2(workGroupID.xy * 64) + FfxInt32x2(x * 2, y * 2);
    FfxInt32x2 pix = FfxInt32x2(workGroupID.xy * 32) + FfxInt32x2(x, y);
    v[0] = SpdReduceLoadSourceImage(tex, slice);
    SpdStore(pix, v[0], 0, slice);

    tex = FfxInt32x2(workGroupID.xy * 64) + FfxInt32x2(x * 2 + 32, y * 2);
    pix = FfxInt32x2(workGroupID.xy * 32) + FfxInt32x2(x + 16, y);
    v[1] = SpdReduceLoadSourceImage(tex, slice);
    SpdStore(pix, v[1], 0, slice);

    tex = FfxInt32x2(workGroupID.xy * 64) + FfxInt32x2(x * 2, y * 2 + 32);
    pix = FfxInt32x2(workGroupID.xy * 32) + FfxInt32x2(x, y + 16);
    v[2] = SpdReduceLoadSourceImage(tex, slice);
    SpdStore(pix, v[2], 0, slice);

    tex = FfxInt32x2(workGroupID.xy * 64) + FfxInt32x2(x * 2 + 32, y * 2 + 32);
    pix = FfxInt32x2(workGroupID.xy * 32) + FfxInt32x2(x + 16, y + 16);
    v[3] = SpdReduceLoadSourceImage(tex, slice);
    SpdStore(pix, v[3], 0, slice);

    if (mip <= 1)
    {
        return;
    }

    v[0] = SpdReduceQuad(v[0]);
    v[1] = SpdReduceQuad(v[1]);
    v[2] = SpdReduceQuad(v[2]);
    v[3] = SpdReduceQuad(v[3]);

    if ((localInvocationIndex % 4) == 0)
    {
        SpdStore(FfxInt32x2(workGroupID.xy * 16) + FfxInt32x2(x / 2, y / 2), v[0], 1, slice);
        SpdStoreIntermediate(x / 2, y / 2, v[0]);

        SpdStore(FfxInt32x2(workGroupID.xy * 16) + FfxInt32x2(x / 2 + 8, y / 2), v[1], 1, slice);
        SpdStoreIntermediate(x / 2 + 8, y / 2, v[1]);

        SpdStore(FfxInt32x2(workGroupID.xy * 16) + FfxInt32x2(x / 2, y / 2 + 8), v[2], 1, slice);
        SpdStoreIntermediate(x / 2, y / 2 + 8, v[2]);

        SpdStore(FfxInt32x2(workGroupID.xy * 16) + FfxInt32x2(x / 2 + 8, y / 2 + 8), v[3], 1, slice);
        SpdStoreIntermediate(x / 2 + 8, y / 2 + 8, v[3]);
    }
}

void SpdDownsampleMip_2(
    FfxUInt32 x,
    FfxUInt32 y,
    FfxUInt32x2 workGroupID,
    FfxUInt32 localInvocationIndex,
    FfxUInt32 mip,
    FfxUInt32 slice)
{
    FfxFloat32x4 v = SpdLoadIntermediate(x, y);
    v = SpdReduceQuad(v);

    // Quad index 0 stores the result.
    if (localInvocationIndex % 4 == 0)
    {
        SpdStore(FfxInt32x2(workGroupID.xy * 8) + FfxInt32x2(x / 2, y / 2), v, mip, slice);
        SpdStoreIntermediate(x + (y / 2) % 2, y, v);
    }
}

void SpdDownsampleMip_3(
    FfxUInt32 x,
    FfxUInt32 y,
    FfxUInt32x2 workGroupID,
    FfxUInt32 localInvocationIndex,
    FfxUInt32 mip,
    FfxUInt32 slice)
{
    if (localInvocationIndex < 64)
    {
        FfxFloat32x4 v = SpdLoadIntermediate(x * 2 + y % 2, y * 2);
        v = SpdReduceQuad(v);

        if (localInvocationIndex % 4 == 0)
        {
            SpdStore(FfxInt32x2(workGroupID.xy * 4) + FfxInt32x2(x / 2, y / 2), v, mip, slice);
            SpdStoreIntermediate(x * 2 + y / 2, y * 2, v);
        }
    }
}

void SpdDownsampleMip_4(
    FfxUInt32 x,
    FfxUInt32 y,
    FfxUInt32x2 workGroupID,
    FfxUInt32 localInvocationIndex,
    FfxUInt32 mip,
    FfxUInt32 slice)
{
    if (localInvocationIndex < 16)
    {
        FfxFloat32x4 v = SpdLoadIntermediate(x * 4 + y, y * 4);
        v = SpdReduceQuad(v);

        if (localInvocationIndex % 4 == 0)
        {
            SpdStore(FfxInt32x2(workGroupID.xy * 2) + FfxInt32x2(x / 2, y / 2), v, mip, slice);
            SpdStoreIntermediate(x / 2 + y, 0, v);
        }
    }
}

void SpdDownsampleMip_5(
    FfxUInt32x2 workGroupID,
    FfxUInt32 localInvocationIndex,
    FfxUInt32 mip,
    FfxUInt32 slice)
{
    if (localInvocationIndex < 4)
    {
        FfxFloat32x4 v = SpdLoadIntermediate(localInvocationIndex, 0);
        v = SpdReduceQuad(v);

        if (localInvocationIndex % 4 == 0)
        {
            SpdStore(FfxInt32x2(workGroupID.xy), v, mip, slice);
        }
    }
}

void SpdDownsampleMips_6_7(FfxUInt32 x, FfxUInt32 y, FfxUInt32 mips, FfxUInt32 slice)
{
    FfxInt32x2 tex = FfxInt32x2(x * 4 + 0, y * 4 + 0);
    FfxInt32x2 pix = FfxInt32x2(x * 2 + 0, y * 2 + 0);
    FfxFloat32x4 v0 = SpdReduceLoad4(tex, slice);
    SpdStore(pix, v0, 6, slice);

    tex = FfxInt32x2(x * 4 + 2, y * 4 + 0);
    pix = FfxInt32x2(x * 2 + 1, y * 2 + 0);
    FfxFloat32x4 v1 = SpdReduceLoad4(tex, slice);
    SpdStore(pix, v1, 6, slice);

    tex = FfxInt32x2(x * 4 + 0, y * 4 + 2);
    pix = FfxInt32x2(x * 2 + 0, y * 2 + 1);
    FfxFloat32x4 v2 = SpdReduceLoad4(tex, slice);
    SpdStore(pix, v2, 6, slice);

    tex = FfxInt32x2(x * 4 + 2, y * 4 + 2);
    pix = FfxInt32x2(x * 2 + 1, y * 2 + 1);
    FfxFloat32x4 v3 = SpdReduceLoad4(tex, slice);
    SpdStore(pix, v3, 6, slice);

    if (mips <= 7)
    {
        return;
    }

    // No barrier needed: only values from this same thread are combined.
    FfxFloat32x4 v = SpdReduce4(v0, v1, v2, v3);
    SpdStore(FfxInt32x2(x, y), v, 7, slice);
    SpdStoreIntermediate(x, y, v);
}

void SpdDownsampleNextFour(
    FfxUInt32 x,
    FfxUInt32 y,
    FfxUInt32x2 workGroupID,
    FfxUInt32 localInvocationIndex,
    FfxUInt32 baseMip,
    FfxUInt32 mips,
    FfxUInt32 slice)
{
    if (mips <= baseMip)
    {
        return;
    }
    ffxSpdWorkgroupShuffleBarrier();
    SpdDownsampleMip_2(x, y, workGroupID, localInvocationIndex, baseMip, slice);

    if (mips <= baseMip + 1)
    {
        return;
    }
    ffxSpdWorkgroupShuffleBarrier();
    SpdDownsampleMip_3(x, y, workGroupID, localInvocationIndex, baseMip + 1, slice);

    if (mips <= baseMip + 2)
    {
        return;
    }
    ffxSpdWorkgroupShuffleBarrier();
    SpdDownsampleMip_4(x, y, workGroupID, localInvocationIndex, baseMip + 2, slice);

    if (mips <= baseMip + 3)
    {
        return;
    }
    ffxSpdWorkgroupShuffleBarrier();
    SpdDownsampleMip_5(workGroupID, localInvocationIndex, baseMip + 3, slice);
}

/// Downsamples a 64x64 tile selected by the work group id. If this turns out to be the
/// last active group, it goes on to compute the remaining mip levels alone.
void SpdDownsample(
    FfxUInt32x2 workGroupID,
    FfxUInt32 localInvocationIndex,
    FfxUInt32 mips,
    FfxUInt32 numWorkGroups,
    FfxUInt32 slice)
{
    // Mip 0 and 1.
    FfxUInt32x2 sub_xy = ffxRemapForWaveReduction(localInvocationIndex % 64);
    FfxUInt32 x = sub_xy.x + 8 * ((localInvocationIndex >> 6) % 2);
    FfxUInt32 y = sub_xy.y + 8 * (localInvocationIndex >> 7);
    SpdDownsampleMips_0_1(x, y, workGroupID, localInvocationIndex, mips, slice);

    // Mips 2 through 5.
    SpdDownsampleNextFour(x, y, workGroupID, localInvocationIndex, 2, mips, slice);

    if (mips <= 6)
    {
        return;
    }

    // Elect the last group still running; everyone else stops here.
    if (SpdExitWorkgroup(numWorkGroups, localInvocationIndex, slice))
    {
        return;
    }

    // Reset the counter for the next dispatch.
    SpdResetAtomicCounter(slice);

    // A single group remains, covering the up-to-64x64 remainder.
    SpdDownsampleMips_6_7(x, y, mips, slice);
    SpdDownsampleNextFour(x, y, FfxUInt32x2(0, 0), localInvocationIndex, 8, mips, slice);
}

void SpdDownsample(
    FfxUInt32x2 workGroupID,
    FfxUInt32 localInvocationIndex,
    FfxUInt32 mips,
    FfxUInt32 numWorkGroups,
    FfxUInt32 slice,
    FfxUInt32x2 workGroupOffset)
{
    SpdDownsample(workGroupID + workGroupOffset, localInvocationIndex, mips, numWorkGroups, slice);
}

#endif // WOLF_FSR3_SPD_INCLUDED
