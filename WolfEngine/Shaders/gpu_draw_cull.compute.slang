import "draw_data.slang";

#define WOLF_MAX_INSTANCE_COUNT 20000
#define WOLF_MAX_MESH_COUNT 20000
#define WOLF_MAX_MATERIAL_COUNT 8192

static const uint DRAW_FLAG_ACTIVE = 1;
static const uint DRAW_FLAG_BUCKET_SHIFT = 1;
static const uint DRAW_FLAG_BUCKET_MASK = 0x7FFFFFFF;

static const uint HARDEN_COUNTER_STALE_REJECT = 0;
static const uint HARDEN_COUNTER_FALLBACK_SUBSTITUTE = 1;
static const uint HARDEN_COUNTER_VISIBLE_CLAMP = 4;
static const uint HARDEN_COUNTER_MATERIAL_FALLBACK_DRAW = 5;

cbuffer CullParams : register(b12)
{
    float4 planes[6];
    float4 cameraPositionAndMaxDrawCount;
    uint bucketCount;
    uint maxVisiblePerBucket;
    uint fallbackMeshHandle;
    uint pad0;
};

RWStructuredBuffer<GpuDrawCommand> g_DrawCommands : register(u0);
RWStructuredBuffer<GpuInstanceData> g_InstanceTable : register(u1);
RWStructuredBuffer<GpuMeshData> g_MeshTable : register(u2);
RWStructuredBuffer<GpuDrawArgs> g_DrawArgs : register(u3);
RWStructuredBuffer<uint> g_DrawCountPerBucket : register(u4);
RWStructuredBuffer<uint> g_VisibleDrawIdsPerBucket : register(u5);
RWStructuredBuffer<uint> g_DrawExecutionRangePerBucket : register(u6);
RWStructuredBuffer<uint> g_DrawGenerations : register(u7);
RWStructuredBuffer<uint> g_InstanceGenerations : register(u8);
RWStructuredBuffer<uint> g_MeshGenerations : register(u9);
RWStructuredBuffer<uint> g_MaterialGenerations : register(u10);
RWStructuredBuffer<uint> g_Diagnostics : register(u11);

bool IsSphereVisible(float3 center, float radius)
{
    for (uint i = 0; i < 6; i++)
    {
        float4 plane = planes[i];
        float dist = dot(plane.xyz, center) + plane.w;
        if (dist < -radius)
        {
            return false;
        }
    }
    return true;
}

[numthreads(8, 8, 1)]
void CSCull(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    float3 cameraPosition = cameraPositionAndMaxDrawCount.xyz;
    uint maxDrawCount = (uint)cameraPositionAndMaxDrawCount.w;
    if (bucketCount == 0)
    {
        return;
    }

    uint dispatchWidth = ((maxDrawCount + 63) / 64) * 8;
    uint drawId = dispatchThreadId.x + (dispatchThreadId.y * dispatchWidth);
    if (drawId >= maxDrawCount)
    {
        return;
    }

    GpuDrawArgs args;
    args.indexCount = 0;
    args.instanceCount = 0;
    args.startIndex = 0;
    args.baseVertex = 0;
    args.startInstance = 0;
    g_DrawArgs[drawId] = args;

    GpuDrawCommand cmd = g_DrawCommands[drawId];
    if ((cmd.flags & DRAW_FLAG_ACTIVE) == 0)
    {
        return;
    }

    if (!IsPackedHandleValid(cmd.drawHandle) ||
        GetHandleIndex(cmd.drawHandle) != drawId ||
        g_DrawGenerations[drawId] != GetHandleGeneration(cmd.drawHandle))
    {
        cmd.flags = 0;
        g_DrawCommands[drawId] = cmd;
        InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_STALE_REJECT], 1);
        return;
    }

    if (!IsPackedHandleValid(cmd.instanceHandle))
    {
        cmd.flags = 0;
        g_DrawCommands[drawId] = cmd;
        InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_STALE_REJECT], 1);
        return;
    }

    uint instanceIndex = GetHandleIndex(cmd.instanceHandle);
    uint instanceGeneration = GetHandleGeneration(cmd.instanceHandle);
    if (instanceIndex == 0 ||
        instanceIndex >= WOLF_MAX_INSTANCE_COUNT ||
        g_InstanceGenerations[instanceIndex] != instanceGeneration)
    {
        cmd.flags = 0;
        g_DrawCommands[drawId] = cmd;
        InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_STALE_REJECT], 1);
        return;
    }

    GpuInstanceData instanceData = g_InstanceTable[instanceIndex];
    uint materialHandle = instanceData.materialHandle;
    if (!IsPackedHandleValid(materialHandle))
    {
        InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_MATERIAL_FALLBACK_DRAW], 1);
    }
    else
    {
        uint materialIndex = GetHandleIndex(materialHandle);
        uint materialGeneration = GetHandleGeneration(materialHandle);
        if (materialIndex == 0 ||
            materialIndex >= WOLF_MAX_MATERIAL_COUNT ||
            g_MaterialGenerations[materialIndex] != materialGeneration)
        {
            InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_MATERIAL_FALLBACK_DRAW], 1);
        }
    }

    uint meshIndex = 0;
    uint meshHandle = instanceData.meshHandle;
    if (IsPackedHandleValid(meshHandle))
    {
        uint candidateIndex = GetHandleIndex(meshHandle);
        uint candidateGeneration = GetHandleGeneration(meshHandle);
        if (candidateIndex < WOLF_MAX_MESH_COUNT &&
            g_MeshGenerations[candidateIndex] == candidateGeneration)
        {
            meshIndex = candidateIndex;
        }
    }

    if (meshIndex == 0)
    {
        meshIndex = GetHandleIndex(fallbackMeshHandle);
        InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_FALLBACK_SUBSTITUTE], 1);
    }

    float3 relativeCenter = instanceData.boundsCenterRadius.xyz - cameraPosition;
    if (IsSphereVisible(relativeCenter, instanceData.boundsCenterRadius.w) == false)
    {
        return;
    }

    GpuMeshData meshData = g_MeshTable[meshIndex];

    uint bucketIndex = (cmd.flags >> DRAW_FLAG_BUCKET_SHIFT) & DRAW_FLAG_BUCKET_MASK;
    if (bucketIndex >= bucketCount)
    {
        bucketIndex = 0;
    }

    uint visibleDrawIndex;
    InterlockedAdd(g_DrawCountPerBucket[bucketIndex], 1, visibleDrawIndex);
    if (visibleDrawIndex >= maxVisiblePerBucket)
    {
        InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_VISIBLE_CLAMP], 1);
        return;
    }

    g_VisibleDrawIdsPerBucket[(bucketIndex * maxVisiblePerBucket) + visibleDrawIndex] = drawId;

    uint rangeBase = bucketIndex * 2;
    InterlockedMax(g_DrawExecutionRangePerBucket[rangeBase + 1], drawId + 1);

    args.indexCount = meshData.indexCount;
    args.instanceCount = 1;
    args.startIndex = 0;
    args.baseVertex = meshData.baseVertex;
    args.startInstance = instanceIndex;
    g_DrawArgs[drawId] = args;
}
