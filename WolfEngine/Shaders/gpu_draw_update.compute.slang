import "draw_data.slang";

static const uint UPDATE_ADD = 0;
static const uint UPDATE_REMOVE = 1;
static const uint UPDATE_TRANSFORM = 2;
static const uint UPDATE_MATERIAL = 3;
static const uint UPDATE_MESH = 4;

cbuffer UpdateParams : register(b5)
{
    uint updateCount;
    uint pad0;
    uint pad1;
    uint pad2;
};

RWStructuredBuffer<GpuDrawUpdateData> g_Updates : register(u0);
RWStructuredBuffer<GpuInstanceData> g_InstanceTable : register(u1);
RWStructuredBuffer<GpuMaterialData> g_MaterialTable : register(u2);
RWStructuredBuffer<GpuMeshData> g_MeshTable : register(u3);
RWStructuredBuffer<GpuDrawCommand> g_DrawCommands : register(u4);

[numthreads(64, 1, 1)]
void CSUpdate(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint index = dispatchThreadId.x;
    if (index >= updateCount)
    {
        return;
    }

    GpuDrawUpdateData update = g_Updates[index];

    if (update.type == UPDATE_REMOVE)
    {
        GpuDrawCommand cmd = g_DrawCommands[update.drawId];
        cmd.flags = 0;
        g_DrawCommands[update.drawId] = cmd;
        return;
    }

    GpuInstanceData instanceData;
    instanceData.world = update.world;
    instanceData.boundsCenterRadius = update.boundsCenterRadius;
    instanceData.materialId = update.materialId;
    instanceData.meshId = update.meshId;
    instanceData.flags = 1;
    instanceData.pad0 = 0;
    g_InstanceTable[update.instanceId] = instanceData;

    if (update.type == UPDATE_ADD || update.type == UPDATE_MATERIAL)
    {
        GpuMaterialData materialData;
        materialData.baseColor = update.baseColor;
        materialData.metallicRoughness = update.metallicRoughness;
        materialData.albedoHandle = update.albedoHandle;
        materialData.metallicRoughnessHandle = update.metallicRoughnessHandle;
        materialData.normalHandle = update.normalHandle;
        materialData.occlusionHandle = update.occlusionHandle;
        materialData.emissiveHandle = update.emissiveHandle;
        materialData.samplerHandle = update.samplerHandle;
        materialData.pad0 = 0;
        materialData.pad1 = 0;
        g_MaterialTable[update.materialId] = materialData;
    }

    if (update.type == UPDATE_ADD || update.type == UPDATE_MESH)
    {
        GpuMeshData meshData;
        meshData.vertexBufferHandle = update.vertexBufferHandle;
        meshData.indexBufferHandle = update.indexBufferHandle;
        meshData.indexCount = update.indexCount;
        meshData.indexFormat = update.indexFormat;
        meshData.baseVertex = update.baseVertex;
        meshData.pad0 = 0;
        meshData.pad1 = 0;
        meshData.pad2 = 0;
        g_MeshTable[update.meshId] = meshData;
    }

    GpuDrawCommand drawCommand;
    drawCommand.instanceId = update.instanceId;
    drawCommand.drawId = update.drawId;
    drawCommand.flags = 1;
    drawCommand.pad0 = 0;
    g_DrawCommands[update.drawId] = drawCommand;
}
