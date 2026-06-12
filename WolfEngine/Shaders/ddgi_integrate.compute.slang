import "common_bindless.slang";
import "ddgi_common.slang";

float4 IntegrateVisibilityTexel(uint2 pixel)
{
    DdgiTexelInfo info = DdgiDecodeAtlasTexel(pixel, visibilityTileInteriorSize);
    if (!info.valid)
    {
        return float4(1.0f, 1.0f, 0.0f, 1.0f);
    }
    if (!DdgiIsProbeUpdateActive(info.probeIndex))
    {
        Texture2D<float4> historyTex = GetTexture(visibilityHistoryReadHandle);
        return historyTex.Load(int3(pixel, 0));
    }

    float3 texelDirection = DdgiOctDecode(DdgiInteriorUv(info.interiorPixel, visibilityTileInteriorSize));
    uint rayCount = DdgiRaySampleCount(visibilityTileInteriorSize);
    Texture2D<float4> traceTex = GetTexture(traceVisibilityReadHandle);

    float2 moments = 0.0f.xx;
    float weightSum = 0.0f;
    for (uint rayIndex = 0u; rayIndex < rayCount; rayIndex++)
    {
        float3 rayDirection = DdgiRayDirection(rayIndex, rayCount, info.probeIndex);
        // Keep directional depth moments local. Broad lobes blend occluder
        // distances across unrelated directions and cause light leaks.
        float weight = pow(saturate(dot(texelDirection, rayDirection)), 64.0f);
        if (weight <= 1e-5f)
        {
            continue;
        }

        uint2 samplePixel = DdgiRaySamplePixel(info.probeIndex, rayIndex, visibilityTileInteriorSize);
        moments += traceTex.Load(int3(samplePixel, 0)).rg * weight;
        weightSum += weight;
    }

    if (weightSum <= 1e-5f)
    {
        return float4(1.0f, 1.0f, 0.0f, 1.0f);
    }

    return float4(moments / weightSum, 0.0f, 1.0f);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 pixel = dispatchThreadId.xy;
    if (pixel.x < visibilityAtlasWidth && pixel.y < visibilityAtlasHeight)
    {
        Texture2D<float4> historyTex = GetTexture(visibilityHistoryReadHandle);
        RWTexture2D<float4> outputTex = GetRWTexture(visibilityHistoryWriteHandle);
        DdgiTexelInfo info = DdgiDecodeAtlasTexel(pixel, visibilityTileInteriorSize);
        float4 traceValue = IntegrateVisibilityTexel(pixel);
        float4 historyValue = historyTex.Load(int3(pixel, 0));
        float historyWeight = info.valid && DdgiProbeHasUsableHistory(info.probeIndex) ? hysteresis : 0.0f;
        outputTex[pixel] = lerp(traceValue, historyValue, historyWeight);
    }
}
