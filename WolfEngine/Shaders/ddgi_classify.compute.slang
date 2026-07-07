import "common_bindless.slang";
import "draw_data.slang";
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

    float3 probePosition = DdgiProbePosition(probeIndex);
    float influenceHalfExtent = max(probeSpacing, 0.001f) + max(viewBias, 0.0f);
    bool enabled = false;
    for (uint drawIndex = 0u; drawIndex < activeDrawCommandUpperBound; drawIndex++)
    {
        GpuDrawCommand command = g_DrawCommands[drawIndex];
        if ((command.flags & DRAW_FLAG_ACTIVE) == 0u || !IsPackedHandleValid(command.instanceHandle))
        {
            continue;
        }

        uint instanceIndex = GetHandleIndex(command.instanceHandle);
        if (instanceIndex == 0u || instanceIndex >= WOLF_MAX_INSTANCE_COUNT)
        {
            continue;
        }

        float4 bounds = g_InstanceTable[instanceIndex].boundsCenterRadius;
        if (DdgiSphereIntersectsProbeInfluence(
            bounds.xyz,
            max(bounds.w, 0.0f),
            probePosition,
            influenceHalfExtent))
        {
            enabled = true;
            break;
        }
    }

    activityTex[statePixel] = enabled ? float4(1.0f, 0.0f, 0.0f, 1.0f) : 0.0f.xxxx;
}
