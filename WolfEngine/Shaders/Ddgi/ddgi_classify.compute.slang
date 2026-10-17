import "../Common/common_bindless.slang";
import "../Common/draw_data.slang";
import "ddgi_common.slang";

static const uint DRAW_FLAG_ACTIVE = 1u;
static const uint WOLF_MAX_INSTANCE_COUNT = 65535u;

StructuredBuffer<GpuDrawCommand> g_DrawCommands : register(t2);
StructuredBuffer<GpuInstanceData> g_InstanceTable : register(t3);

bool DdgiSphereIntersectsProbeInfluence(
    float3 sphereCenter,
    float sphereRadius,
    float3 probePosition,
    float influenceHalfExtent)
{
    float3 delta = abs(sphereCenter - probePosition) - influenceHalfExtent.xxx;
    float3 outside = max(delta, 0.0f.xxx);
    return dot(outside, outside) <= sphereRadius * sphereRadius;
}

[numthreads(64, 1, 1)]
void DdgiProbeActivityClearCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint physicalProbeIndex = dispatchThreadId.x;
    if (physicalProbeIndex >= probeCount)
    {
        return;
    }

    RWTexture2D<uint> marks = GetRWTextureUint(probeActivityMarksHandle);
    marks[uint2(physicalProbeIndex % atlasColumns, physicalProbeIndex / atlasColumns)] = 0u;
}

// A draw visits only probe cells near its bounds. One thread group owns a draw,
// so even a scene-wide bound distributes its probe tests across 64 lanes.
[numthreads(64, 1, 1)]
void DdgiProbeActivityScatterCS(
    uint3 groupId : SV_GroupID,
    uint3 groupThreadId : SV_GroupThreadID)
{
    uint drawIndex = groupId.x;
    if (drawIndex >= activeDrawCommandUpperBound)
    {
        return;
    }

    GpuDrawCommand command = g_DrawCommands[drawIndex];
    if ((command.flags & DRAW_FLAG_ACTIVE) == 0u || !IsPackedHandleValid(command.instanceHandle))
    {
        return;
    }

    uint instanceIndex = GetHandleIndex(command.instanceHandle);
    if (instanceIndex == 0u || instanceIndex >= WOLF_MAX_INSTANCE_COUNT)
    {
        return;
    }

    float4 bounds = g_InstanceTable[instanceIndex].boundsCenterRadius;
    if (!all(isfinite(bounds)))
    {
        return;
    }
    float sphereRadius = max(bounds.w, 0.0f);
    float influenceHalfExtent = max(probeSpacing, 0.001f) + max(viewBias, 0.0f);
    float reach = sphereRadius + influenceHalfExtent;
    float spacing = max(probeSpacing, 0.001f);
    int3 gridMax = int3(probeCountX, probeCountY, probeCountZ) - 1;
    float3 gridLastPosition = origin + float3(gridMax) * spacing;
    if (any(bounds.xyz + reach.xxx < origin) ||
        any(bounds.xyz - reach.xxx > gridLastPosition))
    {
        return;
    }
    // The extra cell absorbs rounding at an exact sphere/AABB boundary;
    // the original intersection test below decides the final result.
    int3 first = clamp(int3(floor((bounds.xyz - reach.xxx - origin) / spacing)) - 1, 0, gridMax);
    int3 last = clamp(int3(ceil((bounds.xyz + reach.xxx - origin) / spacing)) + 1, 0, gridMax);
    if (any(first > last))
    {
        return;
    }

    uint3 size = uint3(last - first + 1);
    uint candidateCount = size.x * size.y * size.z;
    RWTexture2D<uint> marks = GetRWTextureUint(probeActivityMarksHandle);
    for (uint candidate = groupThreadId.x; candidate < candidateCount; candidate += 64u)
    {
        uint3 local = uint3(
            candidate % size.x,
            (candidate / size.x) % size.y,
            candidate / (size.x * size.y));
        uint3 coord = uint3(first) + local;
        uint probeIndex = DdgiLogicalProbeIndex(coord);
        if (DdgiSphereIntersectsProbeInfluence(
            bounds.xyz,
            sphereRadius,
            DdgiProbePosition(probeIndex),
            influenceHalfExtent))
        {
            uint physicalProbeIndex = DdgiPhysicalProbeIndex(probeIndex);
            uint2 statePixel = uint2(physicalProbeIndex % atlasColumns, physicalProbeIndex / atlasColumns);
            InterlockedOr(marks[statePixel], 1u);
        }
    }
}

[numthreads(64, 1, 1)]
void DdgiProbeClassifyCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint dispatchIndex = dispatchThreadId.x;
    RWTexture2D<float4> activityTex = GetRWTexture(probeActivityWriteHandle);

    uint physicalProbeIndex;
    uint probeIndex;
    uint2 statePixel;
    if (classifyCompactDispatch != 0u)
    {
        uint updateFrames = max(probeUpdateFrames, 1u);
        probeIndex = min(probeUpdateFrameIndex, updateFrames - 1u) + dispatchIndex * updateFrames;
        if (probeIndex >= probeCount)
        {
            return;
        }

        physicalProbeIndex = DdgiPhysicalProbeIndex(probeIndex);
        statePixel = uint2(physicalProbeIndex % atlasColumns, physicalProbeIndex / atlasColumns);
    }
    else
    {
        physicalProbeIndex = dispatchIndex;
        if (physicalProbeIndex >= atlasColumns * atlasRows)
        {
            return;
        }

        statePixel = uint2(physicalProbeIndex % atlasColumns, physicalProbeIndex / atlasColumns);
        if (physicalProbeIndex >= probeCount)
        {
            activityTex[statePixel] = 0.0f.xxxx;
            return;
        }

        probeIndex = DdgiLogicalProbeIndexFromPhysical(physicalProbeIndex);
        if (historyValid != 0u &&
            DdgiProbeHasHistory(probeIndex) &&
            !DdgiIsProbeScheduled(probeIndex))
        {
            return;
        }
    }

    RWTexture2D<uint> marks = GetRWTextureUint(probeActivityMarksHandle);
    bool enabled = marks[statePixel] != 0u;
    activityTex[statePixel] = enabled ? float4(1.0f, 0.0f, 0.0f, 1.0f) : 0.0f.xxxx;
}
