import "common_bindless.slang";
import "ddgi_common.slang";

static const float DDGI_SH_BASIS_L0 = 0.28209479177f;
static const float DDGI_SH_BASIS_L1 = 0.48860251190f;

void WriteIrradianceSh(uint2 pixel)
{
    uint probeIndex = pixel.y * atlasColumns + pixel.x;
    if (probeIndex >= probeCount)
    {
        return;
    }

    Texture2D<float4> historyL0Tex = GetTexture(irradianceL0HistoryReadHandle);
    Texture2D<float4> historyLyTex = GetTexture(irradianceLyHistoryReadHandle);
    Texture2D<float4> historyLzTex = GetTexture(irradianceLzHistoryReadHandle);
    Texture2D<float4> historyLxTex = GetTexture(irradianceLxHistoryReadHandle);
    RWTexture2D<float4> outputL0Tex = GetRWTexture(irradianceL0HistoryWriteHandle);
    RWTexture2D<float4> outputLyTex = GetRWTexture(irradianceLyHistoryWriteHandle);
    RWTexture2D<float4> outputLzTex = GetRWTexture(irradianceLzHistoryWriteHandle);
    RWTexture2D<float4> outputLxTex = GetRWTexture(irradianceLxHistoryWriteHandle);

    float4 historyL0 = historyL0Tex.Load(int3(pixel, 0));
    float4 historyLy = historyLyTex.Load(int3(pixel, 0));
    float4 historyLz = historyLzTex.Load(int3(pixel, 0));
    float4 historyLx = historyLxTex.Load(int3(pixel, 0));
    if (!DdgiIsProbeActive(probeIndex))
    {
        outputL0Tex[pixel] = historyL0;
        outputLyTex[pixel] = historyLy;
        outputLzTex[pixel] = historyLz;
        outputLxTex[pixel] = historyLx;
        return;
    }

    uint rayCount = DdgiRaySampleCount(irradianceTileInteriorSize);
    Texture2D<float4> traceTex = GetTexture(traceIrradianceReadHandle);
    float3 coefficientL0 = 0.0f.xxx;
    float3 coefficientLy = 0.0f.xxx;
    float3 coefficientLz = 0.0f.xxx;
    float3 coefficientLx = 0.0f.xxx;

    for (uint rayIndex = 0u; rayIndex < rayCount; rayIndex++)
    {
        uint2 samplePixel = DdgiRaySamplePixel(probeIndex, rayIndex, irradianceTileInteriorSize);
        float3 baseDirection = DdgiRayDirection(rayIndex, rayCount, probeIndex);
        float3 rayDirection = DdgiJitterDirection(baseDirection, samplePixel, probeIndex);
        float3 radiance = traceTex.Load(int3(samplePixel, 0)).rgb;
        coefficientL0 += radiance * DDGI_SH_BASIS_L0;
        coefficientLy += radiance * (DDGI_SH_BASIS_L1 * rayDirection.y);
        coefficientLz += radiance * (DDGI_SH_BASIS_L1 * rayDirection.z);
        coefficientLx += radiance * (DDGI_SH_BASIS_L1 * rayDirection.x);
    }

    float projectionScale = 4.0f * WOLF_PI / float(max(rayCount, 1u));
    float historyWeight = historyValid != 0u ? hysteresis : 0.0f;
    float4 currentL0 = float4(coefficientL0 * projectionScale, 0.0f);
    float4 currentLy = float4(coefficientLy * projectionScale, 0.0f);
    float4 currentLz = float4(coefficientLz * projectionScale, 0.0f);
    float4 currentLx = float4(coefficientLx * projectionScale, 0.0f);
    outputL0Tex[pixel] = lerp(currentL0, historyL0, historyWeight);
    outputLyTex[pixel] = lerp(currentLy, historyLy, historyWeight);
    outputLzTex[pixel] = lerp(currentLz, historyLz, historyWeight);
    outputLxTex[pixel] = lerp(currentLx, historyLx, historyWeight);
}

float4 IntegrateVisibilityTexel(uint2 pixel)
{
    DdgiTexelInfo info = DdgiDecodeAtlasTexel(pixel, visibilityTileInteriorSize);
    if (!info.valid)
    {
        return float4(1.0f, 1.0f, 0.0f, 1.0f);
    }
    if (!DdgiIsProbeActive(info.probeIndex))
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
        float weight = pow(saturate(dot(texelDirection, rayDirection)), 8.0f);
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
    if (pixel.x < atlasColumns && pixel.y < atlasRows)
    {
        WriteIrradianceSh(pixel);
    }

    if (pixel.x < visibilityAtlasWidth && pixel.y < visibilityAtlasHeight)
    {
        Texture2D<float4> historyTex = GetTexture(visibilityHistoryReadHandle);
        RWTexture2D<float4> outputTex = GetRWTexture(visibilityHistoryWriteHandle);
        float4 traceValue = IntegrateVisibilityTexel(pixel);
        float4 historyValue = historyTex.Load(int3(pixel, 0));
        float historyWeight = historyValid != 0u ? hysteresis : 0.0f;
        outputTex[pixel] = lerp(traceValue, historyValue, historyWeight);
    }
}
