import "common_bindless.slang";
import "ddgi_common.slang";

static const float DDGI_RELOCATION_BLEND = 0.01f;

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

    uint physicalProbeIndex = statePixel.y * atlasColumns + statePixel.x;
    RWTexture2D<float4> stateWriteTex = GetRWTexture(probeStateWriteHandle);
    if (physicalProbeIndex >= probeCount)
    {
        stateWriteTex[statePixel] = 0.0f.xxxx;
        return;
    }
    uint probeIndex = DdgiLogicalProbeIndexFromPhysical(physicalProbeIndex);
    bool enabled = DdgiIsProbeEnabled(probeIndex);

    float3 previousOffset = DdgiProbeHasHistory(probeIndex)
        ? DdgiLoadProbeState(probeStateReadHandle, probeIndex).xyz
        : 0.0f.xxx;
    previousOffset = probeRelocationEnabled != 0u
        ? DdgiClampProbeOffset(previousOffset)
        : 0.0f.xxx;
    if (!DdgiIsProbeUpdateActive(probeIndex) || probeRelocationEnabled == 0u)
    {
        stateWriteTex[statePixel] = float4(previousOffset, enabled ? 1.0f : 0.0f);
        return;
    }

    Texture2D<float4> traceVisibilityTex = GetTexture(traceVisibilityReadHandle);
    uint rayCount = DdgiRaySampleCount(visibilityTileInteriorSize);
    float keepDistance = max(probeMinFrontfaceDistance, 0.0f);
    float3 targetOffset = 0.0f.xxx;

    for (uint rayIndex = 0u; rayIndex < rayCount; rayIndex++)
    {
        uint2 samplePixel = DdgiRaySamplePixel(probeIndex, rayIndex, visibilityTileInteriorSize);
        float4 sampleValue = traceVisibilityTex.Load(int3(samplePixel, 0));
        if (sampleValue.z < 0.5f)
        {
            continue;
        }

        float distance = sampleValue.x * max(maxRayDistance, 0.001f);
        if (distance < keepDistance)
        {
            float3 direction = DdgiRayDirection(rayIndex, rayCount, probeIndex);
            targetOffset -= direction * (keepDistance - distance);
        }
    }

    targetOffset = DdgiClampProbeOffset(targetOffset);
    float3 smoothedOffset = lerp(previousOffset, targetOffset, DDGI_RELOCATION_BLEND);
    stateWriteTex[statePixel] = float4(DdgiClampProbeOffset(smoothedOffset), enabled ? 1.0f : 0.0f);
}
