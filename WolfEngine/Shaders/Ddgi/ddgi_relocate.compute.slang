import "../Common/common_bindless.slang";
import "ddgi_common.slang";

RWStructuredBuffer<float4> g_FirstProbeRelocationDiagnostic : register(u2);

static const float DDGI_RELOCATION_MIN_TOLERANCE = 0.001f;

float DdgiRelocationTolerance(float keepDistance)
{
    // Probe state is RGBA16F. Ignore corrections smaller than its worst-case
    // quantization step over the configured relocation range.
    return max(
        DDGI_RELOCATION_MIN_TOLERANCE,
        max(probeMaxRelocationDistance, keepDistance) / 1024.0f);
}

float3 DdgiClampProbeOffsetRadial(float3 offset)
{
    float maxOffset = max(probeMaxRelocationDistance, 0.0f);
    float offsetLength = length(offset);
    return offsetLength > maxOffset && offsetLength > 0.0f
        ? offset * (maxOffset / offsetLength)
        : offset;
}

void DdgiWriteRelocationDiagnostic(
    uint probeIndex,
    uint physicalProbeIndex,
    uint2 statePixel,
    float3 previousOffset,
    uint sourceStatus,
    float3 closestBackfaceDirection,
    float closestBackfaceDistance,
    float3 closestFrontfaceDirection,
    float closestFrontfaceDistance,
    uint backfaceHitCount,
    float3 targetOffset,
    uint decision,
    float3 resultOffset,
    uint resultStatus,
    bool enabled)
{
    if (debugFirstProbeRelocationReadback == 0u ||
        probeIndex != debugProbeRelocationReadbackIndex)
    {
        return;
    }

    uint record = min(relocationIteration, DDGI_RELOCATION_ITERATION_COUNT - 1u) * 9u;
    g_FirstProbeRelocationDiagnostic[record + 0u] = float4(previousOffset, float(probeIndex));
    g_FirstProbeRelocationDiagnostic[record + 1u] = float4(DdgiProbePosition(probeIndex), float(physicalProbeIndex));
    g_FirstProbeRelocationDiagnostic[record + 2u] = float4(closestBackfaceDirection, closestBackfaceDistance);
    g_FirstProbeRelocationDiagnostic[record + 3u] = float4(closestFrontfaceDirection, closestFrontfaceDistance);
    g_FirstProbeRelocationDiagnostic[record + 4u] = float4(targetOffset, float(decision));
    g_FirstProbeRelocationDiagnostic[record + 5u] = float4(resultOffset, float(frameIndex));
    g_FirstProbeRelocationDiagnostic[record + 6u] = float4(
        DdgiProbePosition(probeIndex) + previousOffset,
        float(sourceStatus));
    g_FirstProbeRelocationDiagnostic[record + 7u] = float4(
        float(statePixel.x),
        float(statePixel.y),
        enabled ? 1.0f : 0.0f,
        float(resultStatus));
    g_FirstProbeRelocationDiagnostic[record + 8u] = float4(
        float(backfaceHitCount),
        float(sourceStatus),
        float(relocationIteration),
        0.0f);
}

[numthreads(8, 8, 1)]
void DdgiRelocationSolveCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 statePixel = dispatchThreadId.xy;
    if (statePixel.x >= atlasColumns || statePixel.y >= atlasRows)
    {
        return;
    }

    uint physicalProbeIndex = statePixel.y * atlasColumns + statePixel.x;
    RWTexture2D<float4> stateWriteTex = GetRWTexture(probeStateWriteHandle);
    RWTexture2D<float4> decisionWriteTex = GetRWTexture(probeRelocationDecisionHandle);
    if (physicalProbeIndex >= probeCount)
    {
        stateWriteTex[statePixel] = float4(0.0f.xxx, float(DDGI_PROBE_STATE_DISABLED));
        decisionWriteTex[statePixel] = 0.0f.xxxx;
        return;
    }

    uint probeIndex = DdgiLogicalProbeIndexFromPhysical(physicalProbeIndex);
    bool sourceAvailable = DdgiProbeHasHistory(probeIndex);
    float4 sourceState = sourceAvailable
        ? DdgiLoadProbeState(probeRelocationStateReadHandle, probeIndex)
        : float4(0.0f.xxx, float(DDGI_PROBE_STATE_STABLE));
    float3 previousOffset = probeRelocationEnabled != 0u
        ? DdgiClampProbeOffsetRadial(sourceState.xyz)
        : 0.0f.xxx;
    uint sourceStatus = sourceAvailable
        ? DdgiDecodeProbeState(sourceState.w)
        : DDGI_PROBE_STATE_STABLE;
    bool enabled = DdgiIsProbeEnabled(probeIndex);

    if (!enabled)
    {
        stateWriteTex[statePixel] = float4(0.0f.xxx, float(DDGI_PROBE_STATE_DISABLED));
        decisionWriteTex[statePixel] = 0.0f.xxxx;
        DdgiWriteRelocationDiagnostic(
            probeIndex, physicalProbeIndex, statePixel, previousOffset, sourceStatus,
            0.0f.xxx, maxRayDistance, 0.0f.xxx, maxRayDistance, 0u,
            0.0f.xxx, 0u, 0.0f.xxx, DDGI_PROBE_STATE_DISABLED, false);
        return;
    }

    if (probeRelocationEnabled == 0u)
    {
        stateWriteTex[statePixel] = float4(0.0f.xxx, float(DDGI_PROBE_STATE_STABLE));
        decisionWriteTex[statePixel] = float4(0.0f, 0.0f, 1.0f, 1.0f);
        DdgiWriteRelocationDiagnostic(
            probeIndex, physicalProbeIndex, statePixel, previousOffset, sourceStatus,
            0.0f.xxx, maxRayDistance, 0.0f.xxx, maxRayDistance, 0u,
            0.0f.xxx, 0u, 0.0f.xxx, DDGI_PROBE_STATE_STABLE, true);
        return;
    }

    if (!DdgiIsProbeRelocationActive(probeIndex))
    {
        stateWriteTex[statePixel] = float4(previousOffset, float(sourceStatus));
        decisionWriteTex[statePixel] = float4(0.0f, 0.0f, 1.0f, 1.0f);
        DdgiWriteRelocationDiagnostic(
            probeIndex, physicalProbeIndex, statePixel, previousOffset, sourceStatus,
            0.0f.xxx, maxRayDistance, 0.0f.xxx, maxRayDistance, 0u,
            previousOffset, 0u, previousOffset, sourceStatus, true);
        return;
    }

    Texture2D<float4> traceVisibilityTex = GetTexture(traceVisibilityReadHandle);
    uint backfaceHitCount = 0u;
    float closestBackfaceDistance = maxRayDistance;
    float3 closestBackfaceDirection = 0.0f.xxx;
    float closestFrontfaceDistance = maxRayDistance;
    float3 closestFrontfaceDirection = 0.0f.xxx;
    float farthestFrontfaceDistance = 0.0f;
    float3 farthestFrontfaceDirection = 0.0f.xxx;

    [unroll]
    for (uint rayIndex = 0u; rayIndex < DDGI_RELOCATION_RAY_COUNT; rayIndex++)
    {
        uint2 samplePixel = DdgiRaySamplePixel(probeIndex, rayIndex, visibilityTileInteriorSize);
        float4 sampleValue = traceVisibilityTex.Load(int3(samplePixel, 0));
        bool hit = sampleValue.z >= 0.5f;
        bool backface = hit && sampleValue.w >= 0.5f;
        float distance = hit ? max(sampleValue.x, 0.0f) : maxRayDistance;
        float3 direction = DdgiRelocationRayDirection(rayIndex);
        if (backface)
        {
            backfaceHitCount++;
            if (distance < closestBackfaceDistance)
            {
                closestBackfaceDistance = distance;
                closestBackfaceDirection = direction;
            }
        }
        else
        {
            if (distance < closestFrontfaceDistance)
            {
                closestFrontfaceDistance = distance;
                closestFrontfaceDirection = direction;
            }
            if (distance > farthestFrontfaceDistance)
            {
                farthestFrontfaceDistance = distance;
                farthestFrontfaceDirection = direction;
            }
        }
    }

    float keepDistance = max(probeMinFrontfaceDistance, 0.0f);
    float relocationTolerance = DdgiRelocationTolerance(keepDistance);
    bool insideGeometry =
        float(backfaceHitCount) / float(DDGI_RELOCATION_RAY_COUNT) >
        saturate(probeBackfaceThreshold);
    float3 targetOffset = previousOffset;
    uint decision = 0u;
    bool hasCandidate = false;

    if (insideGeometry)
    {
        targetOffset += closestBackfaceDirection *
            (closestBackfaceDistance + keepDistance * 0.5f);
        decision = 1u;
        hasCandidate = true;
    }
    else if (closestFrontfaceDistance + relocationTolerance < keepDistance)
    {
        if (dot(closestFrontfaceDirection, farthestFrontfaceDirection) <= 0.0f)
        {
            targetOffset += farthestFrontfaceDirection *
                min(farthestFrontfaceDistance, 1.0f);
            decision = 2u;
            hasCandidate = true;
        }
    }
    else if (closestFrontfaceDistance > keepDistance &&
        dot(previousOffset, previousOffset) > relocationTolerance * relocationTolerance)
    {
        float moveBackMargin = min(
            closestFrontfaceDistance - keepDistance,
            length(previousOffset));
        targetOffset += normalize(-previousOffset) * moveBackMargin;
        decision = 3u;
        hasCandidate = true;
    }

    float acceptanceRadius = min(
        max(probeMaxRelocationDistance, 0.0f),
        max(probeSpacing, 0.001f) * 0.45f);
    if (!hasCandidate ||
        dot(targetOffset, targetOffset) >= acceptanceRadius * acceptanceRadius)
    {
        targetOffset = previousOffset;
    }
    uint resultStatus = DDGI_PROBE_STATE_STABLE;
    stateWriteTex[statePixel] = float4(targetOffset, float(resultStatus));
    float3 decisionColor =
        decision == 1u ? float3(1.0f, 0.0f, 0.0f) :
        decision == 2u ? float3(0.0f, 1.0f, 0.0f) :
        decision == 3u ? float3(0.0f, 0.5f, 1.0f) :
        float3(0.0f, 0.0f, 1.0f);
    decisionWriteTex[statePixel] = float4(decisionColor, 1.0f);
    DdgiWriteRelocationDiagnostic(
        probeIndex, physicalProbeIndex, statePixel, previousOffset, sourceStatus,
        closestBackfaceDirection, closestBackfaceDistance,
        closestFrontfaceDirection, closestFrontfaceDistance, backfaceHitCount,
        targetOffset, decision, targetOffset, resultStatus, true);
}
