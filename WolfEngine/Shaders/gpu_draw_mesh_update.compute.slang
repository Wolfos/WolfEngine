import "draw_data.slang";

#define WOLF_MAX_MESH_COUNT 65535

static const uint HARDEN_COUNTER_STALE_REJECT = 0;
static const uint HARDEN_COUNTER_FALLBACK_SUBSTITUTE = 1;

cbuffer UpdateParams : register(b11)
{
    uint updateCount;
    uint pad0;
    uint pad1;
    uint pad2;
};

RWStructuredBuffer<GpuDrawMeshUpdateData> g_Updates : register(u0);
RWStructuredBuffer<GpuMeshData> g_MeshTable : register(u1);
RWStructuredBuffer<uint> g_MeshGenerations : register(u2);
RWStructuredBuffer<uint> g_Diagnostics : register(u3);

[numthreads(64, 1, 1)]
void CSUpdateMesh(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint index = dispatchThreadId.x;
    if (index >= updateCount)
    {
        return;
    }

    GpuDrawMeshUpdateData update = g_Updates[index];
    if (!IsPackedHandleValid(update.meshHandle))
    {
        InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_STALE_REJECT], 1);
        return;
    }

    uint meshIndex = GetHandleIndex(update.meshHandle);
    uint meshGeneration = GetHandleGeneration(update.meshHandle);
    if (meshIndex == 0 || meshIndex >= WOLF_MAX_MESH_COUNT)
    {
        InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_STALE_REJECT], 1);
        return;
    }

    GpuMeshData meshData = (GpuMeshData)0;
    meshData.vertexBufferHandle = update.vertexBufferHandle;
    meshData.indexBufferHandle = update.indexBufferHandle;
    meshData.indexCount = update.indexCount;
    meshData.indexFormat = update.indexFormat;
    meshData.baseVertex = update.baseVertex;
    meshData.startIndex = update.startIndex;
    g_MeshTable[meshIndex] = meshData;
    g_MeshGenerations[meshIndex] = meshGeneration;
}
