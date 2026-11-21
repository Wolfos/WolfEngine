// Samples a texture through the bindless accessors at caller-supplied coordinates and writes the results to a
// buffer. Editor automation uses it to check, through the same path materials and passes read textures, what the
// GPU returns for an imported texture: decoded block compression, cube face layout and the bindless views.
import "../Common/common_bindless.slang";

// b11, not b0: Metal maps constant buffers and the u0/u1 buffers onto the same buffer indices.
cbuffer TextureSampleProbeParams : register(b11)
{
    uint textureHandle;
    uint samplerHandle;
    uint sampleCount;
    // Non-zero samples through GetTextureCube with a direction in coordinate.xyz; zero samples a 2D texture at
    // coordinate.xy.
    uint sampleAsCube;
    float mipLevel;
};

RWStructuredBuffer<float4> g_coordinates : register(u0);
RWStructuredBuffer<float4> g_samples : register(u1);

[numthreads(64, 1, 1)]
void SampleTextureCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= sampleCount)
    {
        return;
    }

    float4 coordinate = g_coordinates[dispatchThreadId.x];
    SamplerState textureSampler = GetSampler(samplerHandle);
    float4 value;
    if (sampleAsCube != 0u)
    {
        value = GetTextureCube(textureHandle).SampleLevel(textureSampler, coordinate.xyz, mipLevel);
    }
    else
    {
        value = GetTexture(textureHandle).SampleLevel(textureSampler, coordinate.xy, mipLevel);
    }

    g_samples[dispatchThreadId.x] = value;
}
