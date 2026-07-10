import "common_bindless.slang";

cbuffer BindlessHandles : register(b0)
{
    uint sourceHandle;
    uint secondaryHandle;
    uint outputHandle;
    uint samplerHandle;
};

cbuffer BloomSettings : register(b1)
{
    uint sourceSizeX;
    uint sourceSizeY;
    uint secondarySizeX;
    uint secondarySizeY;
    uint outputSizeX;
    uint outputSizeY;
    float threshold;
    float softKnee;
    float scatter;
    float intensity;
    float3 tint;
    float _padding0;
};

float3 SampleTent(Texture2D<float4> texture, SamplerState samplerState, float2 uv, float2 textureSize)
{
    float2 texel = rcp(textureSize);
    float3 color = 0.0f.xxx;
    float weight = 0.0f;
    [unroll] for (int y = -1; y <= 1; ++y)
    {
        [unroll] for (int x = -1; x <= 1; ++x)
        {
            float sampleWeight = (x == 0 ? 2.0f : 1.0f) * (y == 0 ? 2.0f : 1.0f);
            color += texture.SampleLevel(samplerState, uv + float2(x, y) * texel, 0.0f).rgb * sampleWeight;
            weight += sampleWeight;
        }
    }
    return color / weight;
}

bool IsOutside(uint3 id) { return id.x >= outputSizeX || id.y >= outputSizeY; }

[numthreads(8, 8, 1)]
void BloomPrefilterCS(uint3 id : SV_DispatchThreadID)
{
    if (IsOutside(id)) return;
    Texture2D<float4> source = GetTexture(sourceHandle);
    RWTexture2D<float4> output = GetRWTexture(outputHandle);
    SamplerState samplerState = GetSampler(samplerHandle);
    float2 uv = (float2(id.xy) + 0.5f) / float2(outputSizeX, outputSizeY);
    float3 color = max(SampleTent(source, samplerState, uv, float2(sourceSizeX, sourceSizeY)), 0.0f.xxx);
    float brightness = max(color.r, max(color.g, color.b));
    float knee = max(threshold * softKnee, 1e-5f);
    float soft = saturate((brightness - threshold + knee) / (2.0f * knee));
    soft = soft * soft * knee;
    float contribution = max(brightness - threshold, soft) / max(brightness, 1e-5f);
    output[id.xy] = float4(color * contribution, 1.0f);
}

[numthreads(8, 8, 1)]
void BloomDownsampleCS(uint3 id : SV_DispatchThreadID)
{
    if (IsOutside(id)) return;
    Texture2D<float4> source = GetTexture(sourceHandle);
    RWTexture2D<float4> output = GetRWTexture(outputHandle);
    float2 uv = (float2(id.xy) + 0.5f) / float2(outputSizeX, outputSizeY);
    output[id.xy] = float4(SampleTent(source, GetSampler(samplerHandle), uv, float2(sourceSizeX, sourceSizeY)), 1.0f);
}

[numthreads(8, 8, 1)]
void BloomUpsampleCS(uint3 id : SV_DispatchThreadID)
{
    if (IsOutside(id)) return;
    Texture2D<float4> small = GetTexture(sourceHandle);
    Texture2D<float4> large = GetTexture(secondaryHandle);
    RWTexture2D<float4> output = GetRWTexture(outputHandle);
    float2 uv = (float2(id.xy) + 0.5f) / float2(outputSizeX, outputSizeY);
    float3 wide = SampleTent(small, GetSampler(samplerHandle), uv, float2(sourceSizeX, sourceSizeY));
    float3 local = large.SampleLevel(GetSampler(samplerHandle), uv, 0.0f).rgb;
    output[id.xy] = float4(local + wide * scatter, 1.0f);
}

[numthreads(8, 8, 1)]
void BloomCompositeCS(uint3 id : SV_DispatchThreadID)
{
    if (IsOutside(id)) return;
    Texture2D<float4> scene = GetTexture(sourceHandle);
    Texture2D<float4> bloom = GetTexture(secondaryHandle);
    RWTexture2D<float4> output = GetRWTexture(outputHandle);
    float2 uv = (float2(id.xy) + 0.5f) / float2(outputSizeX, outputSizeY);
    float3 bloomColor = SampleTent(bloom, GetSampler(samplerHandle), uv, float2(secondarySizeX, secondarySizeY));
    output[id.xy] = float4(scene.Load(int3(id.xy, 0)).rgb + bloomColor * tint * intensity, 1.0f);
}
