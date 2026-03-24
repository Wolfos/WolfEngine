import "common_bindless.slang";

cbuffer BindlessHandles : register(b0)
{
    uint inputHandle;
    uint outputHandle;
    uint samplerHandle;
};

cbuffer TonemappingSettings : register(b1)
{
    uint renderSizeX;
    uint renderSizeY;
    float exposure;
    float _padding0;
};

float3 AcesFitted(float3 color)
{
    const float a = 2.51f;
    const float b = 0.03f;
    const float c = 2.43f;
    const float d = 0.59f;
    const float e = 0.14f;
    return saturate((color * (a * color + b)) / (color * (c * color + d) + e));
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= renderSizeX || dispatchThreadId.y >= renderSizeY)
    {
        return;
    }

    Texture2D<float4> inputTexture = GetTexture(inputHandle);
    RWTexture2D<float4> outputTexture = GetRWTexture(outputHandle);
    SamplerState linearSampler = GetSampler(samplerHandle);

    float2 renderSize = float2(renderSizeX, renderSizeY);
    float2 uv = (float2(dispatchThreadId.xy) + 0.5f) / renderSize;
    float3 hdrColor = inputTexture.SampleLevel(linearSampler, uv, 0.0f).rgb;
    float3 exposedColor = hdrColor * exposure;
    float3 tonemapped = AcesFitted(max(exposedColor, 0.0f.xxx));
    outputTexture[dispatchThreadId.xy] = float4(saturate(tonemapped), 1.0f);
}
