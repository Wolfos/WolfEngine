import "../Common/draw_data.slang";

#define WOLF_MAX_INSTANCE_COUNT 65535
#define WOLF_MAX_MESH_COUNT 65535
#define WOLF_MAX_MATERIAL_COUNT 65535
#define WOLF_MAX_CULL_VIEW_COUNT 3
#define WOLF_FRUSTUM_PLANE_COUNT 6

static const uint DRAW_FLAG_ACTIVE = 1;
static const uint DRAW_FLAG_BUCKET_SHIFT = 1;
static const uint DRAW_FLAG_BUCKET_MASK = 0x7FFFFFFF;

#ifdef WOLF_GPU_DRAW_DIAGNOSTICS
static const uint HARDEN_COUNTER_STALE_REJECT = 0;
static const uint HARDEN_COUNTER_FALLBACK_SUBSTITUTE = 1;
static const uint HARDEN_COUNTER_VISIBLE_CLAMP = 4;
static const uint HARDEN_COUNTER_MATERIAL_FALLBACK_DRAW = 5;
#endif

cbuffer CullParams : register(b12)
{
    float4 planes[WOLF_MAX_CULL_VIEW_COUNT * WOLF_FRUSTUM_PLANE_COUNT];
    float4 cameraPositionAndMaxDrawCount;
    uint viewCount;
    uint bucketCount;
    uint maxVisiblePerBucket;
    uint fallbackMeshHandle;
    uint outputDrawArgsStride;
    uint outputLaneStride;
    uint participatingLaneMask;
    uint pad0;
};

RWStructuredBuffer<GpuDrawCommand> g_DrawCommands : register(u0);
RWStructuredBuffer<GpuInstanceData> g_InstanceTable : register(u1);
RWStructuredBuffer<GpuMeshData> g_MeshTable : register(u2);
RWStructuredBuffer<GpuDrawArgs> g_DrawArgs : register(u3);
#ifdef WOLF_GPU_DRAW_DIAGNOSTICS
RWStructuredBuffer<uint> g_DrawCountPerBucket : register(u4);
RWStructuredBuffer<uint> g_DrawExecutionRangePerBucket : register(u5);
#endif
RWStructuredBuffer<uint> g_DrawGenerations : register(u6);
RWStructuredBuffer<uint> g_InstanceGenerations : register(u7);
RWStructuredBuffer<uint> g_MeshGenerations : register(u8);
#ifdef WOLF_GPU_DRAW_DIAGNOSTICS
RWStructuredBuffer<uint> g_MaterialGenerations : register(u9);
RWStructuredBuffer<uint> g_Diagnostics : register(u10);
#endif

bool IsSphereVisible(uint viewIndex, float3 center, float radius)
{
    uint planeBase = viewIndex * WOLF_FRUSTUM_PLANE_COUNT;
    for (uint i = 0; i < WOLF_FRUSTUM_PLANE_COUNT; i++)
    {
        float4 plane = planes[planeBase + i];
        float dist = dot(plane.xyz, center) + plane.w;
        if (dist < -radius)
        {
            return false;
        }
    }
    return true;
}

[numthreads(64, 1, 1)]
void CSCull(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    float3 cameraPosition = cameraPositionAndMaxDrawCount.xyz;
    uint maxDrawCount = (uint)cameraPositionAndMaxDrawCount.w;
    if (viewCount == 0 || viewCount > WOLF_MAX_CULL_VIEW_COUNT || bucketCount == 0)
    {
        return;
    }

    uint drawId = dispatchThreadId.x;
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
    args.pad0 = 0;
    args.pad1 = 0;
    args.pad2 = 0;
    for (uint viewIndex = 0; viewIndex < viewCount; viewIndex++)
    {
        g_DrawArgs[(viewIndex * outputDrawArgsStride) + drawId] = args;
    }

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
#ifdef WOLF_GPU_DRAW_DIAGNOSTICS
        InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_STALE_REJECT], 1);
#endif
        return;
    }

    if (!IsPackedHandleValid(cmd.instanceHandle))
    {
        cmd.flags = 0;
        g_DrawCommands[drawId] = cmd;
#ifdef WOLF_GPU_DRAW_DIAGNOSTICS
        InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_STALE_REJECT], 1);
#endif
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
#ifdef WOLF_GPU_DRAW_DIAGNOSTICS
        InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_STALE_REJECT], 1);
#endif
        return;
    }

    GpuInstanceData instanceData = g_InstanceTable[instanceIndex];
#ifdef WOLF_GPU_DRAW_DIAGNOSTICS
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
#endif

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
#ifdef WOLF_GPU_DRAW_DIAGNOSTICS
        InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_FALLBACK_SUBSTITUTE], 1);
#endif
    }

    GpuMeshData meshData = g_MeshTable[meshIndex];

    uint bucketIndex = (cmd.flags >> DRAW_FLAG_BUCKET_SHIFT) & DRAW_FLAG_BUCKET_MASK;
    if (bucketIndex >= bucketCount)
    {
        bucketIndex = 0;
    }
    if (bucketIndex >= 32)
    {
        return;
    }
    if ((participatingLaneMask & (1u << bucketIndex)) == 0)
    {
        return;
    }

    float3 relativeCenter = instanceData.boundsCenterRadius.xyz - cameraPosition;
    for (uint viewIndex = 0; viewIndex < viewCount; viewIndex++)
    {
        if (IsSphereVisible(viewIndex, relativeCenter, instanceData.boundsCenterRadius.w) == false)
        {
            continue;
        }

#ifdef WOLF_GPU_DRAW_DIAGNOSTICS
        uint laneIndex = (viewIndex * outputLaneStride) + bucketIndex;
        uint visibleDrawIndex;
        InterlockedAdd(g_DrawCountPerBucket[laneIndex], 1, visibleDrawIndex);
        if (visibleDrawIndex >= maxVisiblePerBucket)
        {
            InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_VISIBLE_CLAMP], 1);
            continue;
        }

        uint rangeBase = laneIndex * 2;
        InterlockedMax(g_DrawExecutionRangePerBucket[rangeBase + 1], drawId + 1);
#endif

        args.indexCount = meshData.indexCount;
        args.instanceCount = 1;
        args.startIndex = 0;
        args.baseVertex = meshData.baseVertex;
        args.startInstance = instanceIndex;
        args.pad0 = 0;
        args.pad1 = 0;
        args.pad2 = 0;
        g_DrawArgs[(viewIndex * outputDrawArgsStride) + drawId] = args;
    }
}
