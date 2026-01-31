import "draw_data.slang";

cbuffer CullParams : register(b5)
{
    float4 planes[6];
    float4 cameraPositionAndMaxDrawCount;
};

RWStructuredBuffer<GpuDrawCommand> g_DrawCommands : register(u0);
RWStructuredBuffer<GpuInstanceData> g_InstanceTable : register(u1);
RWStructuredBuffer<GpuMeshData> g_MeshTable : register(u2);
RWStructuredBuffer<GpuDrawArgs> g_DrawArgs : register(u3);
RWStructuredBuffer<uint> g_DrawCount : register(u4);

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

[numthreads(64, 1, 1)]
void CSCull(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    float3 cameraPosition = cameraPositionAndMaxDrawCount.xyz;
    uint maxDrawCount = (uint)cameraPositionAndMaxDrawCount.w;
    uint drawId = dispatchThreadId.x;
    if (drawId >= maxDrawCount)
    {
        return;
    }

    GpuDrawCommand cmd = g_DrawCommands[drawId];
    if (cmd.flags == 0)
    {
        return;
    }

    GpuInstanceData instanceData = g_InstanceTable[cmd.instanceId];
    float3 relativeCenter = instanceData.boundsCenterRadius.xyz - cameraPosition;
    if (!IsSphereVisible(relativeCenter, instanceData.boundsCenterRadius.w))
    {
        return;
    }

    GpuMeshData meshData = g_MeshTable[instanceData.meshId];

    uint drawIndex;
    InterlockedAdd(g_DrawCount[0], 1, drawIndex);

    GpuDrawArgs args;
    args.indexCount = meshData.indexCount;
    args.instanceCount = 1;
    args.startIndex = 0;
    args.baseVertex = meshData.baseVertex;
    args.startInstance = 0;
    g_DrawArgs[drawIndex] = args;
}
