import "../Common/common_bindless.slang";

cbuffer BindlessHandles : register(b0)
{
    uint sourceHandle;
    uint outputHandle;
    uint samplerHandle;
};

cbuffer ColorPyramidSettings : register(b1)
{
    uint sourceSizeX;
    uint sourceSizeY;
    uint outputSizeX;
    uint outputSizeY;
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

// Level 0 mirrors the resolved scene color one-to-one so mirror-like reflections keep
// full detail. Only the coarser levels are filtered.
[numthreads(8, 8, 1)]
void ColorPyramidCopyCS(uint3 id : SV_DispatchThreadID)
{
    if (IsOutside(id)) return;
    RWTexture2D<float4> output = GetRWTexture(outputHandle);
    float3 color = max(GetTexture(sourceHandle).Load(int3(id.xy, 0)).rgb, 0.0f.xxx);
    output[id.xy] = float4(color, 1.0f);
}

[numthreads(8, 8, 1)]
void ColorPyramidDownsampleCS(uint3 id : SV_DispatchThreadID)
{
    if (IsOutside(id)) return;
    RWTexture2D<float4> output = GetRWTexture(outputHandle);
    float2 uv = (float2(id.xy) + 0.5f) / float2(outputSizeX, outputSizeY);
    float3 color = max(
        SampleTent(GetTexture(sourceHandle), GetSampler(samplerHandle), uv, float2(sourceSizeX, sourceSizeY)),
        0.0f.xxx);
    output[id.xy] = float4(color, 1.0f);
}
