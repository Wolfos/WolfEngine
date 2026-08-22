import "../Common/common.slang";
import "../Common/common_bindless.slang";
import "../Common/common_lighting.slang";

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
    uint traceResolutionX;
    uint traceResolutionY;
    float sharpness;
    float projZBias;
    float projZScale;
    float upsamplePadding;
};

int2 ClampPixel(int2 pixel, uint2 size)
{
    return clamp(pixel, int2(0, 0), int2(int(size.x) - 1, int(size.y) - 1));
}

int2 ResolveRepresentativeFullPixel(int2 tracePixel, uint2 traceSize, uint2 fullSize)
{
    float2 uv = (float2(tracePixel) + 0.5f) / float2(traceSize);
    return ClampPixel(int2(uv * float2(fullSize)), fullSize);
}

float ReconstructViewDepth(float depth)
{
    float denominator = depth - projZBias;
    if (abs(denominator) < 1e-6f)
    {
        denominator = denominator < 0.0f ? -1e-6f : 1e-6f;
    }
    return projZScale / denominator;
}

float NormalWeight(float value)
{
    float value2 = saturate(value) * saturate(value);
    float value4 = value2 * value2;
    float value8 = value4 * value4;
    float value16 = value8 * value8;
    return value16 * value16;
}

[numthreads(8, 8, 1)]
void ReflectionsUpsampleCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 fullSize = uint2(fullResolutionX, fullResolutionY);
    uint2 fullPixel = dispatchThreadId.xy;
    if (any(fullPixel >= fullSize))
    {
        return;
    }

    Texture2D<float4> depthTexture = GetTexture(depthHandle);
    RWTexture2D<float4> outputTexture = GetRWTexture(outputHandle);
    float centerDepth = depthTexture.Load(int3(fullPixel, 0)).r;
    if (IsBackgroundDepth(centerDepth))
    {
        outputTexture[fullPixel] = 0.0f.xxxx;
        return;
    }

    Texture2D<float4> normalTexture = GetTexture(normalHandle);
    Texture2D<float4> sourceTexture = GetTexture(sourceHandle);
    uint2 traceSize = uint2(traceResolutionX, traceResolutionY);
    float centerViewDepth = abs(ReconstructViewDepth(centerDepth));
    float3 centerNormal = DecodeNormal(normalTexture.Load(int3(fullPixel, 0)).xyz);
    float2 tracePosition =
        (float2(fullPixel) + 0.5f) / float2(fullSize) * float2(traceSize) - 0.5f;
    int2 traceBase = int2(floor(tracePosition));
    float2 traceFraction = saturate(tracePosition - float2(traceBase));

    float4 reflectionSum = 0.0f.xxxx;
    float weightSum = 0.0f;
    for (int y = 0; y < 2; y++)
    {
        for (int x = 0; x < 2; x++)
        {
            int2 tracePixel = ClampPixel(traceBase + int2(x, y), traceSize);
            int2 representativePixel = ResolveRepresentativeFullPixel(tracePixel, traceSize, fullSize);
            float sampleDepth = depthTexture.Load(int3(representativePixel, 0)).r;
            if (IsBackgroundDepth(sampleDepth))
            {
                continue;
            }

            float sampleViewDepth = abs(ReconstructViewDepth(sampleDepth));
            float3 sampleNormal = DecodeNormal(normalTexture.Load(int3(representativePixel, 0)).xyz);
            float bilinearWeight =
                (x == 0 ? 1.0f - traceFraction.x : traceFraction.x) *
                (y == 0 ? 1.0f - traceFraction.y : traceFraction.y);
            float depthScale = rcp(max(min(centerViewDepth, sampleViewDepth), 1e-3f));
            float depthWeight = exp2(
                -abs(sampleViewDepth - centerViewDepth) * depthScale * sharpness);
            float weight = bilinearWeight * depthWeight * NormalWeight(dot(centerNormal, sampleNormal));
            reflectionSum += sourceTexture.Load(int3(tracePixel, 0)) * weight;
            weightSum += weight;
        }
    }

    if (weightSum > 1e-5f)
    {
        outputTexture[fullPixel] = reflectionSum / weightSum;
        return;
    }

    int2 nearestTracePixel = ClampPixel(int2(round(tracePosition)), traceSize);
    outputTexture[fullPixel] = sourceTexture.Load(int3(nearestTracePixel, 0));
}
