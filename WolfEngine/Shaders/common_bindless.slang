static const uint DESCRIPTOR_KIND_SRV = 0;
static const uint DESCRIPTOR_KIND_UAV = 1;
static const uint DESCRIPTOR_KIND_CBV = 2;
static const uint DESCRIPTOR_KIND_SAMPLER = 3;

static const uint DESCRIPTOR_INDEX_MASK = 0x3FFFFFFF;
static const uint DESCRIPTOR_KIND_SHIFT = 30;
static const uint BINDLESS_UAV_BASE = 0;

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
    return g_TextureHeap.g_Textures[GetDescriptorIndex(handle)];
}

#if !defined(WOLF_NO_RW_TEXTURES)
RWTexture2D<float4> GetRWTexture(uint handle)
{
    return g_RWTextureHeap.g_RWTextures[GetDescriptorIndex(handle)];
}
#endif

SamplerState GetSampler(uint handle)
{
    return g_SamplerHeap.g_Samplers[GetDescriptorIndex(handle)];
}
