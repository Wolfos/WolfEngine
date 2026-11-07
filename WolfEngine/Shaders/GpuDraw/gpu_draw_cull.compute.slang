import "../Common/draw_data.slang";

#define WOLF_MAX_INSTANCE_COUNT 1048576
#define WOLF_MAX_MESH_COUNT 1048576
#define WOLF_MAX_MATERIAL_COUNT 1048576
#define WOLF_MAX_CULL_VIEW_COUNT 3
// Mirrors FrustumCulling.MaxPlaneCount; camera views use six, shadow volumes may use more.
#define WOLF_CULL_PLANE_COUNT 24

// Mirrors GpuDrawFlags in GpuDrawData.cs.
static const uint DRAW_FLAG_ACTIVE = 1;
static const uint DRAW_FLAG_BUCKET_SHIFT = 1;
static const uint DRAW_FLAG_BUCKET_MASK = 0x1F;
static const uint DRAW_FLAG_OWNER_VIEW_SHIFT = 6;
static const uint DRAW_FLAG_OWNER_VIEW_MASK = 0x7F;

#ifdef WOLF_GPU_DRAW_DIAGNOSTICS
static const uint HARDEN_COUNTER_STALE_REJECT = 0;
static const uint HARDEN_COUNTER_FALLBACK_SUBSTITUTE = 1;
static const uint HARDEN_COUNTER_VISIBLE_CLAMP = 4;
static const uint HARDEN_COUNTER_MATERIAL_FALLBACK_DRAW = 5;
#endif

cbuffer CullParams : register(b12)
{
    float4 planes[WOLF_MAX_CULL_VIEW_COUNT * WOLF_CULL_PLANE_COUNT];
    uint planeCounts[WOLF_MAX_CULL_VIEW_COUNT];
    float4 cameraPositionAndMaxDrawCount;
    uint viewCount;
    uint bucketCount;
    uint maxVisiblePerBucket;
    uint fallbackMeshHandle;
    // Draw args per view; also the number of batch slots.
    uint outputDrawArgsStride;
    uint outputLaneStride;
    uint participatingLaneMask;
    // Slot of the render view being culled. Every view's draws share one command table.
    uint ownerViewIndex;
    // One past the highest live instance index.
    uint maxInstanceCount;
    // Visible instance list entries per view.
    uint visibleInstanceStride;
};

RWStructuredBuffer<GpuDrawCommand> g_DrawCommands : register(u0);
RWStructuredBuffer<GpuInstanceData> g_InstanceTable : register(u1);
RWStructuredBuffer<GpuDrawArgs> g_DrawArgs : register(u3);
#ifdef WOLF_GPU_DRAW_DIAGNOSTICS
RWStructuredBuffer<uint> g_DrawCountPerBucket : register(u4);
RWStructuredBuffer<uint> g_DrawExecutionRangePerBucket : register(u5);
#endif
RWStructuredBuffer<uint> g_VisibleInstances : register(u6);
RWStructuredBuffer<uint> g_InstanceGenerations : register(u7);
#ifdef WOLF_GPU_DRAW_DIAGNOSTICS
RWStructuredBuffer<uint> g_MeshGenerations : register(u8);
RWStructuredBuffer<uint> g_MaterialGenerations : register(u9);
RWStructuredBuffer<uint> g_Diagnostics : register(u10);
#endif
RWStructuredBuffer<GpuDrawBatchData> g_Batches : register(u11);

bool IsSphereVisible(uint viewIndex, float3 center, float radius)
{
    uint planeBase = viewIndex * WOLF_CULL_PLANE_COUNT;
    for (uint i = 0; i < planeCounts[viewIndex]; i++)
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

// Resets every batch's draw args in every culled view before CSCull appends visible instances to them. The args
// point the batch at its slice of the view's visible list, so the vertex shader reads instance ids from there.
[numthreads(64, 1, 1)]
void CSPrepareBatchArgs(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint maxBatchCount = (uint)cameraPositionAndMaxDrawCount.w;
    if (viewCount == 0 || viewCount > WOLF_MAX_CULL_VIEW_COUNT)
    {
        return;
    }

    uint batchIndex = dispatchThreadId.x;
    if (batchIndex >= maxBatchCount)
    {
        return;
    }

    GpuDrawBatchData batch = g_Batches[batchIndex];
    for (uint viewIndex = 0; viewIndex < viewCount; viewIndex++)
    {
        GpuDrawArgs args;
        args.indexCount = 0;
        args.instanceCount = 0;
        args.startIndex = 0;
        args.baseVertex = 0;
        args.startInstance = (viewIndex * visibleInstanceStride) + batch.visibleOffset;
        args.pad0 = 0;
        args.pad1 = 0;
        args.pad2 = 0;
        g_DrawArgs[(viewIndex * outputDrawArgsStride) + batchIndex] = args;
    }
}

// One thread per instance: appends each visible instance to its batch's visible list in every culled view.
[numthreads(64, 1, 1)]
void CSCull(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    float3 cameraPosition = cameraPositionAndMaxDrawCount.xyz;
    uint maxBatchCount = (uint)cameraPositionAndMaxDrawCount.w;
    if (viewCount == 0 || viewCount > WOLF_MAX_CULL_VIEW_COUNT || bucketCount == 0)
    {
        return;
    }

    uint instanceIndex = dispatchThreadId.x;
    if (instanceIndex == 0 || instanceIndex >= maxInstanceCount || instanceIndex >= WOLF_MAX_INSTANCE_COUNT)
    {
        return;
    }

    GpuDrawCommand cmd = g_DrawCommands[instanceIndex];
    if ((cmd.flags & DRAW_FLAG_ACTIVE) == 0)
    {
        return;
    }

    // Another view's instance: this view never draws it. Checked before generation validation so this view never
    // marks another view's command stale.
    if (((cmd.flags >> DRAW_FLAG_OWNER_VIEW_SHIFT) & DRAW_FLAG_OWNER_VIEW_MASK) != ownerViewIndex)
    {
        return;
    }

    if (!IsPackedHandleValid(cmd.instanceHandle) ||
        GetHandleIndex(cmd.instanceHandle) != instanceIndex ||
        g_InstanceGenerations[instanceIndex] != GetHandleGeneration(cmd.instanceHandle))
    {
        cmd.flags = 0;
        g_DrawCommands[instanceIndex] = cmd;
#ifdef WOLF_GPU_DRAW_DIAGNOSTICS
        InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_STALE_REJECT], 1);
#endif
        return;
    }

    uint batchIndex = cmd.batchIndex;
    if (batchIndex == 0 || batchIndex >= maxBatchCount)
    {
        return;
    }

    GpuDrawBatchData batch = g_Batches[batchIndex];
    if (batch.capacity == 0)
    {
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

    uint meshHandle = instanceData.meshHandle;
    bool meshValid = false;
    if (IsPackedHandleValid(meshHandle))
    {
        uint candidateIndex = GetHandleIndex(meshHandle);
        uint candidateGeneration = GetHandleGeneration(meshHandle);
        meshValid = candidateIndex != 0 &&
            candidateIndex < WOLF_MAX_MESH_COUNT &&
            g_MeshGenerations[candidateIndex] == candidateGeneration;
    }

    if (!meshValid)
    {
        InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_FALLBACK_SUBSTITUTE], 1);
    }
#endif

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
        InterlockedMax(g_DrawExecutionRangePerBucket[rangeBase + 1], batchIndex + 1);
#endif

        uint argsIndex = (viewIndex * outputDrawArgsStride) + batchIndex;
        uint slot;
        InterlockedAdd(g_DrawArgs[argsIndex].instanceCount, 1, slot);
        // The batch table and the command table are written in the same frame, so a member past capacity means
        // they disagree. Its slot would belong to the next batch, so it is dropped; compaction and the vertex
        // shader clamp the count to capacity.
        if (slot >= batch.capacity)
        {
#ifdef WOLF_GPU_DRAW_DIAGNOSTICS
            InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_VISIBLE_CLAMP], 1);
#endif
            continue;
        }

        g_VisibleInstances[(viewIndex * visibleInstanceStride) + batch.visibleOffset + slot] = instanceIndex;
    }
}
