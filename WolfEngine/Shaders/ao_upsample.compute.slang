import "common_bindless.slang";
import "common_lighting.slang";

cbuffer BindlessHandles : register(b0)
{
    uint depthHandle;
    uint normalHandle;
    uint sourceHandle;
    uint outputHandle;
};

cbuffer UpsampleSettings : register(b1)
{
    uint fullResolutionX;
    uint fullResolutionY;
    uint aoResolutionX;
    uint aoResolutionY;
};

int2 ClampFullPixel(int2 pixel, uint2 size)
{
    return clamp(pixel, int2(0, 0), int2(int(size.x) - 1, int(size.y) - 1));
}

int2 ClampAoPixel(int2 pixel, uint2 size)
{
    return clamp(pixel, int2(0, 0), int2(int(size.x) - 1, int(size.y) - 1));
}

int2 ResolveRepresentativeFullPixel(int2 aoPixel, uint2 aoSize, uint2 fullSize)
{
    float2 uv = (float2(aoPixel) + 0.5f) / float2(aoSize);
    return ClampFullPixel(int2(uv * float2(fullSize)), fullSize);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 fullSize = uint2(fullResolutionX, fullResolutionY);
    if (dispatchThreadId.x >= fullSize.x || dispatchThreadId.y >= fullSize.y)
    {
        return;
    }

    uint2 aoSize = uint2(aoResolutionX, aoResolutionY);
    Texture2D<float4> depthTexture = GetTexture(depthHandle);
    Texture2D<float4> normalTexture = GetTexture(normalHandle);
    Texture2D<float4> sourceTexture = GetTexture(sourceHandle);
    RWTexture2D<float4> outputTexture = GetRWTexture(outputHandle);

    int2 fullPixel = int2(dispatchThreadId.xy);
    float centerDepth = depthTexture.Load(int3(fullPixel, 0)).r;
    if (centerDepth >= 0.9999f)
    {
        outputTexture[dispatchThreadId.xy] = float4(1.0f, 1.0f, 1.0f, 1.0f);
        return;
    }

    float3 centerNormal = DecodeNormal(normalTexture.Load(int3(fullPixel, 0)).xyz);
    float2 lowPosition = (float2(fullPixel) + 0.5f) / float2(fullSize) * float2(aoSize) - 0.5f;
    int2 lowBase = int2(floor(lowPosition));
    float2 lowFrac = saturate(lowPosition - float2(lowBase));

    float aoSum = 0.0f;
    float weightSum = 0.0f;
    for (int y = 0; y < 2; ++y)
    {
        for (int x = 0; x < 2; ++x)
        {
            int2 aoPixel = ClampAoPixel(lowBase + int2(x, y), aoSize);
            float aoValue = sourceTexture.Load(int3(aoPixel, 0)).r;
            int2 repFull = ResolveRepresentativeFullPixel(aoPixel, aoSize, fullSize);
            float sampleDepth = depthTexture.Load(int3(repFull, 0)).r;
            float3 sampleNormal = DecodeNormal(normalTexture.Load(int3(repFull, 0)).xyz);

            float bilinearWeight = (x == 0 ? 1.0f - lowFrac.x : lowFrac.x) * (y == 0 ? 1.0f - lowFrac.y : lowFrac.y);
            float depthWeight = exp(-abs(sampleDepth - centerDepth) * 64.0f);
            float normalWeight = pow(saturate(dot(centerNormal, sampleNormal)), 32.0f);
            float weight = bilinearWeight * depthWeight * normalWeight;
            aoSum += aoValue * weight;
            weightSum += weight;
        }
    }

    float ao = weightSum > 0.0f ? aoSum / weightSum : 1.0f;
    outputTexture[dispatchThreadId.xy] = float4(ao, ao, ao, 1.0f);
}
