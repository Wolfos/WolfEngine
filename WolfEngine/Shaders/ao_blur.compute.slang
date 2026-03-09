import "common_bindless.slang";
import "common_lighting.slang";

cbuffer BindlessHandles : register(b0)
{
    uint depthHandle;
    uint normalHandle;
    uint sourceHandle;
    uint outputHandle;
    uint samplerHandle;
};

cbuffer BlurSettings : register(b1)
{
    uint fullResolutionX;
    uint fullResolutionY;
    uint aoResolutionX;
    uint aoResolutionY;
    uint blurHorizontally;
    float blurSharpness;
    float2 _blurPadding;
};

int2 ClampAoPixel(int2 pixel, uint2 size)
{
    return clamp(pixel, int2(0, 0), int2(int(size.x) - 1, int(size.y) - 1));
}

int2 ResolveFullPixel(float2 uv, uint2 fullSize)
{
    return clamp(int2(uv * float2(fullSize)), int2(0, 0), int2(int(fullSize.x) - 1, int(fullSize.y) - 1));
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 aoSize = uint2(aoResolutionX, aoResolutionY);
    if (dispatchThreadId.x >= aoSize.x || dispatchThreadId.y >= aoSize.y)
    {
        return;
    }

    uint2 fullSize = uint2(fullResolutionX, fullResolutionY);
    Texture2D<float4> depthTexture = GetTexture(depthHandle);
    Texture2D<float4> normalTexture = GetTexture(normalHandle);
    Texture2D<float4> sourceTexture = GetTexture(sourceHandle);
    RWTexture2D<float4> outputTexture = GetRWTexture(outputHandle);

    int2 centerAo = int2(dispatchThreadId.xy);
    float2 centerUv = (float2(centerAo) + 0.5f) / float2(aoSize);
    int2 centerFull = ResolveFullPixel(centerUv, fullSize);
    float centerDepth = depthTexture.Load(int3(centerFull, 0)).r;
    if (centerDepth >= 0.9999f)
    {
        outputTexture[dispatchThreadId.xy] = float4(1.0f, 1.0f, 1.0f, 1.0f);
        return;
    }

    float3 centerNormal = DecodeNormal(normalTexture.Load(int3(centerFull, 0)).xyz);

    float weightSum = 0.0f;
    float aoSum = 0.0f;
    for (int tap = -3; tap <= 3; ++tap)
    {
        int2 sampleAo = centerAo + (blurHorizontally != 0u ? int2(tap, 0) : int2(0, tap));
        sampleAo = ClampAoPixel(sampleAo, aoSize);
        float2 sampleUv = (float2(sampleAo) + 0.5f) / float2(aoSize);
        int2 sampleFull = ResolveFullPixel(sampleUv, fullSize);

        float sampleDepth = depthTexture.Load(int3(sampleFull, 0)).r;
        float3 sampleNormal = DecodeNormal(normalTexture.Load(int3(sampleFull, 0)).xyz);
        float sampleAoValue = sourceTexture.Load(int3(sampleAo, 0)).r;

        float spatialWeight = exp(-0.5f * float(tap * tap));
        float depthWeight = exp(-abs(sampleDepth - centerDepth) * blurSharpness * 64.0f);
        float normalWeight = pow(saturate(dot(centerNormal, sampleNormal)), 32.0f);
        float weight = spatialWeight * depthWeight * normalWeight;
        aoSum += sampleAoValue * weight;
        weightSum += weight;
    }

    float ao = weightSum > 0.0f ? aoSum / weightSum : sourceTexture.Load(int3(centerAo, 0)).r;
    outputTexture[dispatchThreadId.xy] = float4(ao, ao, ao, 1.0f);
}
