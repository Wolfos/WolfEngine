import "common_bindless.slang";
import "ddgi_common.slang";

float3 DdgiClampProbeOffset(float3 offset)
{
    float maxOffset = max(probeMaxRelocationDistance, 0.0f);
    return clamp(offset, -maxOffset.xxx, maxOffset.xxx);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 statePixel = dispatchThreadId.xy;
    if (statePixel.x >= atlasColumns || statePixel.y >= atlasRows)
    {
        return;
    }

    uint probeIndex = statePixel.y * atlasColumns + statePixel.x;
    RWTexture2D<float4> stateWriteTex = GetRWTexture(probeStateWriteHandle);
    if (probeIndex >= probeCount)
    {
        stateWriteTex[statePixel] = 0.0f.xxxx;
        return;
    }

    float3 previousOffset = historyValid != 0u
        ? DdgiLoadProbeState(probeStateReadHandle, probeIndex).xyz
        : 0.0f.xxx;
    previousOffset = probeRelocationEnabled != 0u
        ? DdgiClampProbeOffset(previousOffset)
        : 0.0f.xxx;
    if (!DdgiIsProbeActive(probeIndex) || probeRelocationEnabled == 0u)
    {
        stateWriteTex[statePixel] = float4(previousOffset, 0.0f);
        return;
    }

    Texture2D<float4> traceVisibilityTex = GetTexture(traceVisibilityReadHandle);
    uint rayCount = DdgiRaySampleCount(visibilityTileInteriorSize);
    uint backfaceHitCount = 0u;
    float closestFrontfaceDistance = maxRayDistance;
    float closestBackfaceDistance = maxRayDistance;
    float3 closestFrontfaceDirection = 0.0f.xxx;
    float3 closestBackfaceDirection = 0.0f.xxx;

    for (uint rayIndex = 0u; rayIndex < rayCount; rayIndex++)
    {
        uint2 samplePixel = DdgiRaySamplePixel(probeIndex, rayIndex, visibilityTileInteriorSize);
        float4 sampleValue = traceVisibilityTex.Load(int3(samplePixel, 0));
        if (sampleValue.z < 0.5f)
        {
            continue;
        }

        float distance = sampleValue.x * max(maxRayDistance, 0.001f);
        float3 direction = DdgiRayDirection(rayIndex, rayCount, probeIndex);
        if (sampleValue.w >= 0.5f)
        {
            backfaceHitCount++;
            if (distance < closestBackfaceDistance)
            {
                closestBackfaceDistance = distance;
                closestBackfaceDirection = direction;
            }
        }
        else if (distance < closestFrontfaceDistance)
        {
            closestFrontfaceDistance = distance;
            closestFrontfaceDirection = direction;
        }
    }

    float3 requestedOffset = previousOffset;
    float backfaceRatio = float(backfaceHitCount) / float(max(rayCount, 1u));
    bool insideGeometry = backfaceRatio > probeBackfaceThreshold;
    if (insideGeometry && closestBackfaceDistance < maxRayDistance)
    {
        requestedOffset += closestBackfaceDirection * (closestBackfaceDistance + probeMinFrontfaceDistance);
    }
    else if (closestFrontfaceDistance < probeMinFrontfaceDistance)
    {
        requestedOffset -= closestFrontfaceDirection * (probeMinFrontfaceDistance - closestFrontfaceDistance);
    }

    stateWriteTex[statePixel] = float4(DdgiClampProbeOffset(requestedOffset), 0.0f);
}
