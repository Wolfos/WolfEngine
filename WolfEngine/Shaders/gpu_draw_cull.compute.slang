import "draw_data.slang";

#define WOLF_MAX_INSTANCE_COUNT 20000
#define WOLF_MAX_MESH_COUNT 20000

static const uint DRAW_FLAG_ACTIVE = 1;
static const uint DRAW_FLAG_BUCKET_SHIFT = 1;
static const uint DRAW_FLAG_BUCKET_MASK = 0x7FFFFFFF;

cbuffer CullParams : register(b7)
{
    float4 planes[6];
    float4 cameraPositionAndMaxDrawCount;
    uint bucketCount;
    uint pad0;
    uint pad1;
    uint pad2;
};

RWStructuredBuffer<GpuDrawCommand> g_DrawCommands : register(u0);
RWStructuredBuffer<GpuInstanceData> g_InstanceTable : register(u1);
RWStructuredBuffer<GpuMeshData> g_MeshTable : register(u2);
RWStructuredBuffer<GpuDrawArgs> g_DrawArgs : register(u3);
RWStructuredBuffer<uint> g_DrawCountPerBucket : register(u4);
RWStructuredBuffer<uint> g_VisibleDrawIdsPerBucket : register(u5);
RWStructuredBuffer<uint> g_DrawExecutionRangePerBucket : register(u6);

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

    if (cmd.instanceId >= WOLF_MAX_INSTANCE_COUNT)
    {
        return;
    }

    GpuInstanceData instanceData = g_InstanceTable[cmd.instanceId];
    if (instanceData.meshId >= WOLF_MAX_MESH_COUNT)
    {
        return;
    }
      
    // View frustum culling
    float3 relativeCenter = instanceData.boundsCenterRadius.xyz - cameraPosition;
    if (IsSphereVisible(relativeCenter, instanceData.boundsCenterRadius.w) == false)
    {
        return;
    }
    

    GpuMeshData meshData = g_MeshTable[instanceData.meshId];

    uint bucketIndex = (cmd.flags >> DRAW_FLAG_BUCKET_SHIFT) & DRAW_FLAG_BUCKET_MASK;
    if (bucketIndex >= bucketCount)
    {
        bucketIndex = 0;
    }

    uint visibleDrawIndex;
    InterlockedAdd(g_DrawCountPerBucket[bucketIndex], 1, visibleDrawIndex);
    g_VisibleDrawIdsPerBucket[(bucketIndex * maxDrawCount) + visibleDrawIndex] = drawId;
    InterlockedMax(g_DrawExecutionRangePerBucket[(bucketIndex * 2) + 1], drawId + 1);

    args.indexCount = meshData.indexCount;
    args.instanceCount = 1;
    args.startIndex = 0;
    args.baseVertex = meshData.baseVertex;
    args.startInstance = cmd.instanceId;
    g_DrawArgs[drawId] = args;
}
