import "common_bindless.slang";
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
    if (debugFirstProbeRelocationReadback == 0u || probeIndex != 0u)
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
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
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
    bool sourceAvailable = relocationIteration > 0u || DdgiProbeHasHistory(probeIndex);
    float4 sourceState = sourceAvailable
        ? DdgiLoadProbeState(probeRelocationStateReadHandle, probeIndex)
        : float4(0.0f.xxx, float(DDGI_PROBE_STATE_DISABLED));
    float3 previousOffset = probeRelocationEnabled != 0u
        ? DdgiClampProbeOffsetRadial(sourceState.xyz)
        : 0.0f.xxx;
    uint sourceStatus = sourceAvailable
        ? DdgiDecodeProbeState(sourceState.w)
        : DDGI_PROBE_STATE_DISABLED;
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

    if (!DdgiIsProbeRelocationActive(probeIndex, sourceState))
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
    float distances[DDGI_RELOCATION_RAY_COUNT];
    uint backfaceHitCount = 0u;
    float closestBackfaceDistance = maxRayDistance;
    float3 closestBackfaceDirection = 0.0f.xxx;
    float closestFrontfaceDistance = maxRayDistance;
    float3 closestFrontfaceDirection = 0.0f.xxx;

    [unroll]
    for (uint rayIndex = 0u; rayIndex < DDGI_RELOCATION_RAY_COUNT; rayIndex++)
    {
        uint2 samplePixel = DdgiRaySamplePixel(probeIndex, rayIndex, visibilityTileInteriorSize);
        float4 sampleValue = traceVisibilityTex.Load(int3(samplePixel, 0));
        bool hit = sampleValue.z >= 0.5f;
        bool backface = hit && sampleValue.w >= 0.5f;
        float distance = hit ? max(sampleValue.x, 0.0f) : maxRayDistance;
        float3 direction = DdgiRelocationRayDirection(rayIndex);
        distances[rayIndex] = distance;

        if (backface)
        {
            backfaceHitCount++;
            if (distance < closestBackfaceDistance)
            {
                closestBackfaceDistance = distance;
                closestBackfaceDirection = direction;
            }
        }
        else if (hit && distance < closestFrontfaceDistance)
        {
            closestFrontfaceDistance = distance;
            closestFrontfaceDirection = direction;
        }
    }

    float keepDistance = max(probeMinFrontfaceDistance, 0.0f);
    float relocationTolerance = DdgiRelocationTolerance(keepDistance);
    bool insideGeometry =
        float(backfaceHitCount) / float(DDGI_RELOCATION_RAY_COUNT) >
        saturate(probeBackfaceThreshold);
    float3 targetOffset = previousOffset;
    uint decision = 0u;
    bool correctionRequired = false;

    if (insideGeometry)
    {
        targetOffset += closestBackfaceDirection *
            (closestBackfaceDistance + max(normalBias, 0.001f) + keepDistance);
        decision = 1u;
        correctionRequired = true;
    }
    else if (closestFrontfaceDistance + relocationTolerance < keepDistance)
    {
        float openDistance = -1.0f;
        float3 openDirection = 0.0f.xxx;
        [unroll]
        for (uint rayIndex = 0u; rayIndex < DDGI_RELOCATION_RAY_COUNT; rayIndex++)
        {
            float3 direction = DdgiRelocationRayDirection(rayIndex);
            if (dot(direction, closestFrontfaceDirection) <= 0.0f &&
                distances[rayIndex] > openDistance)
            {
                openDistance = distances[rayIndex];
                openDirection = direction;
            }
        }

        float requiredSeparation = keepDistance - closestFrontfaceDistance;
        float availableClearance = max(openDistance - keepDistance, 0.0f);
        float movement = min(requiredSeparation, availableClearance);
        if (movement > relocationTolerance)
        {
            targetOffset += openDirection * movement;
            decision = 2u;
        }
        else
        {
            decision = 4u;
        }
        correctionRequired = true;
    }
    targetOffset = DdgiClampProbeOffsetRadial(targetOffset);
    float progress = length(targetOffset - previousOffset);
    uint resultStatus;
    if (correctionRequired)
    {
        resultStatus = progress > relocationTolerance
            ? DDGI_PROBE_STATE_RELOCATING
            : DDGI_PROBE_STATE_BLOCKED;
    }
    else
    {
        resultStatus = DDGI_PROBE_STATE_STABLE;
    }

    if (relocationIteration == DDGI_RELOCATION_ITERATION_COUNT - 1u &&
        resultStatus == DDGI_PROBE_STATE_RELOCATING)
    {
        float3 frameStartOffset = DdgiProbeHasHistory(probeIndex)
            ? DdgiClampProbeOffsetRadial(
                DdgiLoadProbeState(probeStateReadHandle, probeIndex).xyz)
            : 0.0f.xxx;
        targetOffset = frameStartOffset;
        resultStatus = DDGI_PROBE_STATE_BLOCKED;
        decision = 4u;
    }

    stateWriteTex[statePixel] = float4(targetOffset, float(resultStatus));
    float3 decisionColor =
        decision == 1u ? float3(1.0f, 0.0f, 0.0f) :
        decision == 2u ? float3(0.0f, 1.0f, 0.0f) :
        decision == 3u ? float3(0.0f, 0.5f, 1.0f) :
        decision == 4u ? float3(1.0f, 0.5f, 0.0f) :
        float3(0.0f, 0.0f, 1.0f);
    decisionWriteTex[statePixel] = float4(decisionColor, 1.0f);
    DdgiWriteRelocationDiagnostic(
        probeIndex, physicalProbeIndex, statePixel, previousOffset, sourceStatus,
        closestBackfaceDirection, closestBackfaceDistance,
        closestFrontfaceDirection, closestFrontfaceDistance, backfaceHitCount,
        targetOffset, decision, targetOffset, resultStatus, true);
}
