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

uint SafeDescriptorIndex(uint handle)
{
    uint kind = GetDescriptorKind(handle);
    uint index = GetDescriptorIndex(handle);
    if (kind > DESCRIPTOR_KIND_SAMPLER)
    {
        return WOLF_INVALID_DESCRIPTOR_INDEX;
    }
    if (kind == DESCRIPTOR_KIND_SRV && index >= g_BindlessSrvCount)
    {
        return WOLF_INVALID_DESCRIPTOR_INDEX;
    }
    if (kind == DESCRIPTOR_KIND_UAV && index >= g_BindlessUavCount)
    {
        return WOLF_INVALID_DESCRIPTOR_INDEX;
    }
    if (kind == DESCRIPTOR_KIND_SAMPLER && index >= g_BindlessSamplerCount)
    {
        return WOLF_INVALID_DESCRIPTOR_INDEX;
    }
    return index;
}

struct DescriptorHeapTextures
{
#if defined(WOLF_BINDLESS_FIXED_SIZE)
    [[id(0)]] Texture2D<float4> g_Textures[WOLF_BINDLESS_MAX];
#else
    [[id(0)]] Texture2D<float4> g_Textures[];
#endif
};

struct DescriptorHeapSamplers
{
#if defined(WOLF_BINDLESS_FIXED_SIZE)
    [[id(0)]] SamplerState g_Samplers[WOLF_BINDLESS_MAX];
#else
    [[id(0)]] SamplerState g_Samplers[];
#endif
};

[[argument_buffer]] ParameterBlock<DescriptorHeapTextures> g_TextureHeap : register(b28, space1);
#if !defined(WOLF_NO_RW_TEXTURES)
struct DescriptorHeapRWTextures
{
#if defined(WOLF_BINDLESS_FIXED_SIZE)
    [[id(0)]] RWTexture2D<float4> g_RWTextures[WOLF_BINDLESS_MAX];
#else
    [[id(0)]] RWTexture2D<float4> g_RWTextures[];
#endif
};

[[argument_buffer]] ParameterBlock<DescriptorHeapRWTextures> g_RWTextureHeap : register(b29, space1);
#endif
[[argument_buffer]] ParameterBlock<DescriptorHeapSamplers> g_SamplerHeap : register(b30, space1);

Texture2D<float4> GetTexture(uint handle)
{
    uint index = SafeDescriptorIndex(handle);
    if (index == WOLF_INVALID_DESCRIPTOR_INDEX)
    {
        return g_TextureHeap.g_Textures[0];
    }
    return g_TextureHeap.g_Textures[index];
}

#if !defined(WOLF_NO_RW_TEXTURES)
RWTexture2D<float4> GetRWTexture(uint handle)
{
    uint index = SafeDescriptorIndex(handle);
    if (index == WOLF_INVALID_DESCRIPTOR_INDEX)
    {
        return g_RWTextureHeap.g_RWTextures[0];
    }
    return g_RWTextureHeap.g_RWTextures[index];
}
#endif

SamplerState GetSampler(uint handle)
{
    uint index = SafeDescriptorIndex(handle);
    if (index == WOLF_INVALID_DESCRIPTOR_INDEX)
    {
        return g_SamplerHeap.g_Samplers[0];
    }
    return g_SamplerHeap.g_Samplers[index];
}
