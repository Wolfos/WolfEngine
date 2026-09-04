import "../Common/common_bindless.slang";
import "ddgi_common.slang";
import "ddgi_visibility.slang";

static const uint DDGI_VISIBILITY_RESOLUTION = 16u;
static const uint DDGI_VISIBILITY_TEXEL_COUNT =
    DDGI_VISIBILITY_RESOLUTION * DDGI_VISIBILITY_RESOLUTION;

groupshared float4 g_RayVisibility[DDGI_VISIBILITY_TEXEL_COUNT];
groupshared float3 g_RayDirections[DDGI_VISIBILITY_TEXEL_COUNT];
groupshared float4 g_FinalVisibility[DDGI_VISIBILITY_TEXEL_COUNT];
groupshared uint g_UpdateActive;
groupshared uint g_UsableHistory;

uint2 DdgiVisibilityBorderLocalPixel(uint borderIndex)
{
    uint tileSize = visibilityTileInteriorSize + tileBorderSize * 2u;
    uint horizontalBorderCount = tileSize * 2u;
    if (borderIndex < tileSize)
    {
        return uint2(borderIndex, 0u);
    }
    if (borderIndex < horizontalBorderCount)
    {
        return uint2(borderIndex - tileSize, tileSize - 1u);
    }

    uint verticalIndex = borderIndex - horizontalBorderCount;
    if (verticalIndex < visibilityTileInteriorSize)
    {
        return uint2(0u, tileBorderSize + verticalIndex);
    }
    return uint2(
        tileSize - 1u,
        tileBorderSize + verticalIndex - visibilityTileInteriorSize);
}

[numthreads(16, 16, 1)]
void DdgiVisibilityIntegrateCS(
    uint3 groupId : SV_GroupID,
    uint3 groupThreadId : SV_GroupThreadID,
    uint groupIndex : SV_GroupIndex)
{
    uint probeIndex = groupId.x;
    if (probeIndex >= probeCount)
    {
        return;
    }

    if (groupIndex == 0u)
    {
        g_UpdateActive = DdgiIsProbeUpdateActive(probeIndex) ? 1u : 0u;
        g_UsableHistory = DdgiProbeHasUsableHistory(probeIndex) ? 1u : 0u;
    }
    GroupMemoryBarrierWithGroupSync();

    uint2 tileOrigin = DdgiProbeTileOrigin(probeIndex, visibilityTileInteriorSize);
    uint2 interiorPixel = groupThreadId.xy;
    uint2 outputPixel = tileOrigin + tileBorderSize.xx + interiorPixel;
    Texture2D<float4> historyTex = GetTexture(visibilityHistoryReadHandle);
    RWTexture2D<float4> outputTex = GetRWTexture(visibilityHistoryWriteHandle);
    float4 historyValue = historyTex.Load(int3(outputPixel, 0));
    float4 finalValue = historyValue;

    if (g_UpdateActive != 0u)
    {
        uint rayCount = DdgiRaySampleCount(visibilityTileInteriorSize);
        if (groupIndex < rayCount)
        {
            uint2 samplePixel = DdgiRaySamplePixel(
                probeIndex,
                groupIndex,
                visibilityTileInteriorSize);
            g_RayVisibility[groupIndex] =
                GetTexture(traceVisibilityReadHandle).Load(int3(samplePixel, 0));
            g_RayDirections[groupIndex] =
                DdgiRayDirection(groupIndex, rayCount, probeIndex);
        }
        GroupMemoryBarrierWithGroupSync();

        float3 texelDirection = DdgiOctDecode(
            DdgiInteriorUv(interiorPixel, visibilityTileInteriorSize));
        float filterExponent = clamp(float(rayCount) / 6.0f - 1.0f, 8.0f, 32.0f);
        float2 moments = 0.0f.xx;
        float2 hitMoments = 0.0f.xx;
        float weightSum = 0.0f;
        float hitWeightSum = 0.0f;
        for (uint rayIndex = 0u; rayIndex < rayCount; rayIndex++)
        {
            float weight = pow(
                saturate(dot(texelDirection, g_RayDirections[rayIndex])),
                filterExponent);
            if (weight <= 1e-5f)
            {
                continue;
            }

            float4 rayVisibility = g_RayVisibility[rayIndex];
            moments += rayVisibility.rg * weight;
            weightSum += weight;
            float hitWeight = weight * saturate(rayVisibility.b);
            hitMoments += rayVisibility.rg * hitWeight;
            hitWeightSum += hitWeight;
        }

        float4 traceValue = float4(1.0f, 1.0f, 0.0f, 1.0f);
        if (weightSum > 1e-5f)
        {
            float2 allMoments = moments / weightSum;
            float hitCoverage = saturate(hitWeightSum / weightSum);
            float2 localHitMoments = hitWeightSum > 1e-5f
                ? hitMoments / hitWeightSum
                : allMoments;
            traceValue.rg = DdgiBlendVisibilityMoments(
                allMoments,
                localHitMoments,
                hitCoverage);
            traceValue.b = hitCoverage;
        }
        float historyWeight = g_UsableHistory != 0u ? hysteresis : 0.0f;
        finalValue = lerp(traceValue, historyValue, historyWeight);
    }

    g_FinalVisibility[groupIndex] = finalValue;
    outputTex[outputPixel] = finalValue;
    GroupMemoryBarrierWithGroupSync();

    uint tileSize = visibilityTileInteriorSize + tileBorderSize * 2u;
    uint borderTexelCount = tileSize * 2u + visibilityTileInteriorSize * 2u;
    if (groupIndex < borderTexelCount)
    {
        uint2 borderLocalPixel = DdgiVisibilityBorderLocalPixel(groupIndex);
        int2 sourceInteriorPixel = clamp(
            int2(borderLocalPixel) - int2(tileBorderSize, tileBorderSize),
            int2(0, 0),
            int2(visibilityTileInteriorSize - 1u, visibilityTileInteriorSize - 1u));
        uint sourceIndex =
            uint(sourceInteriorPixel.y) * visibilityTileInteriorSize +
            uint(sourceInteriorPixel.x);
        outputTex[tileOrigin + borderLocalPixel] = g_FinalVisibility[sourceIndex];
    }
}
