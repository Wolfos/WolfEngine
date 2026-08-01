static const uint DESCRIPTOR_KIND_SRV = 0;
static const uint DESCRIPTOR_KIND_UAV = 1;
static const uint DESCRIPTOR_KIND_CBV = 2;
static const uint DESCRIPTOR_KIND_SAMPLER = 3;

static const uint DESCRIPTOR_INDEX_MASK = 0x3FFFFFFF;
static const uint DESCRIPTOR_KIND_SHIFT = 30;
static const uint BINDLESS_UAV_BASE = 0;
static const uint WOLF_INVALID_DESCRIPTOR_INDEX = 0xFFFFFFFF;

#ifndef WOLF_BINDLESS_MAX
#define WOLF_BINDLESS_MAX 65536
#endif

#ifndef WOLF_BINDLESS_TEXTURE_MAX
#define WOLF_BINDLESS_TEXTURE_MAX WOLF_BINDLESS_MAX
#endif

#ifndef WOLF_BINDLESS_RW_TEXTURE_MAX
#define WOLF_BINDLESS_RW_TEXTURE_MAX WOLF_BINDLESS_MAX
#endif

#ifndef WOLF_BINDLESS_SAMPLER_MAX
#define WOLF_BINDLESS_SAMPLER_MAX WOLF_BINDLESS_MAX
#endif

uint GetDescriptorKind(uint handle)
{
    return (handle >> DESCRIPTOR_KIND_SHIFT);
}

uint GetDescriptorIndex(uint handle)
{
    return handle & DESCRIPTOR_INDEX_MASK;
}

cbuffer BindlessCounts : register(b27)
{
    uint g_BindlessSrvCount;
    uint g_BindlessUavCount;
    uint g_BindlessSamplerCount;
    uint g_BindlessPadding;
};

uint SafeDescriptorIndex(uint handle, uint expectedKind, uint descriptorCount)
{
    uint kind = GetDescriptorKind(handle);
    uint index = GetDescriptorIndex(handle);
    if (kind != expectedKind || index >= descriptorCount)
    {
        return WOLF_INVALID_DESCRIPTOR_INDEX;
    }
    return index;
}

#if defined(WOLF_TARGET_METAL)
struct DescriptorHeapTextures
{
#if defined(WOLF_BINDLESS_FIXED_SIZE)
    Texture2D<float4> g_Textures[WOLF_BINDLESS_TEXTURE_MAX];
#else
    Texture2D<float4> g_Textures[];
#endif
};

struct DescriptorHeapSamplers
{
#if defined(WOLF_BINDLESS_FIXED_SIZE)
    SamplerState g_Samplers[WOLF_BINDLESS_SAMPLER_MAX];
#else
    SamplerState g_Samplers[];
#endif
};

[[argument_buffer]] ParameterBlock<DescriptorHeapTextures> g_TextureHeap : register(b28, space1);
#if !defined(WOLF_NO_RW_TEXTURES)
// Three views over the same UAV descriptors - same handle, same index, different element
// type or coherency. A texture is only legal to access through the view matching how it
// was created and how it is used.
//
//   g_RWTextures         the default float4 view
//   g_RWTexturesUint     R32Uint, for integer atomics
//   g_RWTexturesCoherent globallycoherent float4, for writes that must be visible to
//                        other thread groups within the same dispatch
//
// On Metal all three are members of a single argument buffer, laid out in declaration
// order, so index i of view n sits at n * WOLF_BINDLESS_RW_TEXTURE_MAX + i.
// MetalDescriptorTable.EncodeUav writes all three slots; keep that in sync with the
// order and count here.
struct DescriptorHeapRWTextures
{
#if defined(WOLF_BINDLESS_FIXED_SIZE)
    RWTexture2D<float4> g_RWTextures[WOLF_BINDLESS_RW_TEXTURE_MAX];
    RWTexture2D<uint> g_RWTexturesUint[WOLF_BINDLESS_RW_TEXTURE_MAX];
    globallycoherent RWTexture2D<float4> g_RWTexturesCoherent[WOLF_BINDLESS_RW_TEXTURE_MAX];
#else
    RWTexture2D<float4> g_RWTextures[];
    RWTexture2D<uint> g_RWTexturesUint[];
    globallycoherent RWTexture2D<float4> g_RWTexturesCoherent[];
#endif
};

[[argument_buffer]] ParameterBlock<DescriptorHeapRWTextures> g_RWTextureHeap : register(b29, space1);
#endif
[[argument_buffer]] ParameterBlock<DescriptorHeapSamplers> g_SamplerHeap : register(b30, space1);

Texture2D<float4> GetTexture(uint handle)
{
    uint index = SafeDescriptorIndex(handle, DESCRIPTOR_KIND_SRV, g_BindlessSrvCount);
    if (index == WOLF_INVALID_DESCRIPTOR_INDEX)
    {
        return g_TextureHeap.g_Textures[0];
    }
    return g_TextureHeap.g_Textures[index];
}

#if !defined(WOLF_NO_RW_TEXTURES)
RWTexture2D<float4> GetRWTexture(uint handle)
{
    uint index = SafeDescriptorIndex(handle, DESCRIPTOR_KIND_UAV, g_BindlessUavCount);
    if (index == WOLF_INVALID_DESCRIPTOR_INDEX)
    {
        return g_RWTextureHeap.g_RWTextures[0];
    }
    return g_RWTextureHeap.g_RWTextures[index];
}

/// Same descriptor as GetRWTexture, viewed as a single-channel uint image. Only valid
/// for textures created as TextureFormat.R32Uint; required for integer atomics, which
/// a float-typed UAV cannot express.
RWTexture2D<uint> GetRWTextureUint(uint handle)
{
    uint index = SafeDescriptorIndex(handle, DESCRIPTOR_KIND_UAV, g_BindlessUavCount);
    if (index == WOLF_INVALID_DESCRIPTOR_INDEX)
    {
        return g_RWTextureHeap.g_RWTexturesUint[0];
    }
    return g_RWTextureHeap.g_RWTexturesUint[index];
}

/// Coherent load/store over the same descriptor as GetRWTexture. Needed when other thread
/// groups in the same dispatch must observe the write - single-pass downsample being the
/// case that motivated it. Costs bandwidth, so only use where cross-group visibility is
/// actually required.
///
/// These are load/store functions rather than a GetRWTextureCoherent() returning the
/// texture, because 'globallycoherent' is a property of the declaration and cannot be
/// carried on a function return type. Keeping the access here keeps the qualifier.
float4 LoadRWTextureCoherent(uint handle, int2 pixel)
{
    uint index = SafeDescriptorIndex(handle, DESCRIPTOR_KIND_UAV, g_BindlessUavCount);
    if (index == WOLF_INVALID_DESCRIPTOR_INDEX)
    {
        return g_RWTextureHeap.g_RWTexturesCoherent[0][pixel];
    }
    return g_RWTextureHeap.g_RWTexturesCoherent[index][pixel];
}

void StoreRWTextureCoherent(uint handle, int2 pixel, float4 value)
{
    uint index = SafeDescriptorIndex(handle, DESCRIPTOR_KIND_UAV, g_BindlessUavCount);
    if (index == WOLF_INVALID_DESCRIPTOR_INDEX)
    {
        return;
    }
    g_RWTextureHeap.g_RWTexturesCoherent[index][pixel] = value;
}
#endif

SamplerState GetSampler(uint handle)
{
    uint index = SafeDescriptorIndex(handle, DESCRIPTOR_KIND_SAMPLER, g_BindlessSamplerCount);
    if (index == WOLF_INVALID_DESCRIPTOR_INDEX)
    {
        return g_SamplerHeap.g_Samplers[0];
    }
    return g_SamplerHeap.g_Samplers[index];
}
#else
#if defined(WOLF_BINDLESS_FIXED_SIZE)
Texture2D<float4> g_Textures[WOLF_BINDLESS_TEXTURE_MAX] : register(t0, space1);
SamplerState g_Samplers[WOLF_BINDLESS_SAMPLER_MAX] : register(s0, space1);
#if !defined(WOLF_NO_RW_TEXTURES)
RWTexture2D<float4> g_RWTextures[WOLF_BINDLESS_RW_TEXTURE_MAX] : register(u0, space1);
// Alias the same descriptor table range as g_RWTextures, in further register spaces.
// The matching D3D12_DESCRIPTOR_RANGEs are added to the bindless UAV root parameter in
// D3D12Device; all of them share OffsetInDescriptorsFromTableStart = 0.
RWTexture2D<uint> g_RWTexturesUint[WOLF_BINDLESS_RW_TEXTURE_MAX] : register(u0, space2);
globallycoherent RWTexture2D<float4> g_RWTexturesCoherent[WOLF_BINDLESS_RW_TEXTURE_MAX] : register(u0, space3);
#endif
#else
Texture2D<float4> g_Textures[] : register(t0, space1);
SamplerState g_Samplers[] : register(s0, space1);
#if !defined(WOLF_NO_RW_TEXTURES)
RWTexture2D<float4> g_RWTextures[] : register(u0, space1);
RWTexture2D<uint> g_RWTexturesUint[] : register(u0, space2);
globallycoherent RWTexture2D<float4> g_RWTexturesCoherent[] : register(u0, space3);
#endif
#endif

Texture2D<float4> GetTexture(uint handle)
{
    uint index = SafeDescriptorIndex(handle, DESCRIPTOR_KIND_SRV, g_BindlessSrvCount);
    if (index == WOLF_INVALID_DESCRIPTOR_INDEX)
    {
        return g_Textures[0];
    }
    return g_Textures[index];
}

#if !defined(WOLF_NO_RW_TEXTURES)
RWTexture2D<float4> GetRWTexture(uint handle)
{
    uint index = SafeDescriptorIndex(handle, DESCRIPTOR_KIND_UAV, g_BindlessUavCount);
    if (index == WOLF_INVALID_DESCRIPTOR_INDEX)
    {
        return g_RWTextures[0];
    }
    return g_RWTextures[index];
}

/// Same descriptor as GetRWTexture, viewed as a single-channel uint image. Only valid
/// for textures created as TextureFormat.R32Uint; required for integer atomics, which
/// a float-typed UAV cannot express.
RWTexture2D<uint> GetRWTextureUint(uint handle)
{
    uint index = SafeDescriptorIndex(handle, DESCRIPTOR_KIND_UAV, g_BindlessUavCount);
    if (index == WOLF_INVALID_DESCRIPTOR_INDEX)
    {
        return g_RWTexturesUint[0];
    }
    return g_RWTexturesUint[index];
}

/// Coherent load/store over the same descriptor as GetRWTexture. Needed when other thread
/// groups in the same dispatch must observe the write - single-pass downsample being the
/// case that motivated it. Costs bandwidth, so only use where cross-group visibility is
/// actually required.
///
/// These are load/store functions rather than a GetRWTextureCoherent() returning the
/// texture, because 'globallycoherent' is a property of the declaration and cannot be
/// carried on a function return type. Keeping the access here keeps the qualifier.
float4 LoadRWTextureCoherent(uint handle, int2 pixel)
{
    uint index = SafeDescriptorIndex(handle, DESCRIPTOR_KIND_UAV, g_BindlessUavCount);
    if (index == WOLF_INVALID_DESCRIPTOR_INDEX)
    {
        return g_RWTexturesCoherent[0][pixel];
    }
    return g_RWTexturesCoherent[index][pixel];
}

void StoreRWTextureCoherent(uint handle, int2 pixel, float4 value)
{
    uint index = SafeDescriptorIndex(handle, DESCRIPTOR_KIND_UAV, g_BindlessUavCount);
    if (index == WOLF_INVALID_DESCRIPTOR_INDEX)
    {
        return;
    }
    g_RWTexturesCoherent[index][pixel] = value;
}
#endif

SamplerState GetSampler(uint handle)
{
    uint index = SafeDescriptorIndex(handle, DESCRIPTOR_KIND_SAMPLER, g_BindlessSamplerCount);
    if (index == WOLF_INVALID_DESCRIPTOR_INDEX)
    {
        return g_Samplers[0];
    }
    return g_Samplers[index];
}
#endif
