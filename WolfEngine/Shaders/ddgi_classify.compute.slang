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

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 statePixel = dispatchThreadId.xy;
    if (statePixel.x >= atlasColumns || statePixel.y >= atlasRows)
    {
        return;
    }

    uint physicalProbeIndex = statePixel.y * atlasColumns + statePixel.x;
    RWTexture2D<float4> activityTex = GetRWTexture(probeActivityWriteHandle);
    if (physicalProbeIndex >= probeCount)
    {
        activityTex[statePixel] = 0.0f.xxxx;
        return;
    }

    uint probeIndex = DdgiLogicalProbeIndexFromPhysical(physicalProbeIndex);
    if (historyValid != 0u &&
        DdgiProbeHasHistory(probeIndex) &&
        !DdgiIsProbeScheduled(probeIndex))
    {
        return;
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
