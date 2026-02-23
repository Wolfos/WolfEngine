import "draw_data.slang";

#define WOLF_MAX_DRAW_COUNT 20000
#define WOLF_MAX_INSTANCE_COUNT 20000
#define WOLF_MAX_MATERIAL_COUNT 8192
#define WOLF_MAX_MESH_COUNT 20000

static const uint UPDATE_ADD = 0;
static const uint UPDATE_REMOVE = 1;
static const uint UPDATE_TRANSFORM = 2;
static const uint UPDATE_MATERIAL = 3;
static const uint UPDATE_MESH = 4;
static const uint DRAW_FLAG_ACTIVE = 1;
static const uint FALLBACK_HANDLE = (1u << WOLF_HANDLE_GEN_SHIFT);

static const uint HARDEN_COUNTER_STALE_REJECT = 0;
static const uint HARDEN_COUNTER_FALLBACK_SUBSTITUTE = 1;

cbuffer UpdateParams : register(b11)
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
RWStructuredBuffer<uint> g_DrawGenerations : register(u5);
RWStructuredBuffer<uint> g_InstanceGenerations : register(u6);
RWStructuredBuffer<uint> g_MeshGenerations : register(u7);
RWStructuredBuffer<uint> g_MaterialGenerations : register(u8);
RWStructuredBuffer<uint> g_Diagnostics : register(u9);

uint NextGeneration(uint generation)
{
    generation = generation + 1;
    if (generation == 0)
    {
        generation = 1;
    }
    return generation;
}

[numthreads(8, 8, 1)]
void CSUpdate(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint dispatchWidth = ((updateCount + 63) / 64) * 8;
    uint index = dispatchThreadId.x + (dispatchThreadId.y * dispatchWidth);
    if (index >= updateCount)
    {
        return;
    }

    GpuDrawUpdateData update = g_Updates[index];
    if (!IsPackedHandleValid(update.drawHandle))
    {
        InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_STALE_REJECT], 1);
        return;
    }

    uint drawIndex = GetHandleIndex(update.drawHandle);
    uint drawGeneration = GetHandleGeneration(update.drawHandle);
    if (drawIndex == 0 || drawIndex >= WOLF_MAX_DRAW_COUNT)
    {
        InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_STALE_REJECT], 1);
        return;
    }

    if (update.type == UPDATE_REMOVE)
    {
        GpuDrawCommand cmd = g_DrawCommands[drawIndex];
        cmd.flags = 0;
        g_DrawCommands[drawIndex] = cmd;
        g_DrawGenerations[drawIndex] = NextGeneration(drawGeneration);

        if (IsPackedHandleValid(update.instanceHandle))
        {
            uint instanceIndex = GetHandleIndex(update.instanceHandle);
            uint instanceGeneration = GetHandleGeneration(update.instanceHandle);
            if (instanceIndex > 0 && instanceIndex < WOLF_MAX_INSTANCE_COUNT)
            {
                g_InstanceGenerations[instanceIndex] = NextGeneration(instanceGeneration);
            }
        }

        return;
    }

    if (!IsPackedHandleValid(update.instanceHandle))
    {
        InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_STALE_REJECT], 1);
        return;
    }

    uint instanceIndex = GetHandleIndex(update.instanceHandle);
    uint instanceGeneration = GetHandleGeneration(update.instanceHandle);
    if (instanceIndex == 0 || instanceIndex >= WOLF_MAX_INSTANCE_COUNT)
    {
        InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_STALE_REJECT], 1);
        return;
    }

    uint meshHandle = update.meshHandle;
    uint materialHandle = update.materialHandle;

    if (!IsPackedHandleValid(meshHandle) || GetHandleIndex(meshHandle) >= WOLF_MAX_MESH_COUNT)
    {
        meshHandle = FALLBACK_HANDLE;
        InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_FALLBACK_SUBSTITUTE], 1);
    }

    if (!IsPackedHandleValid(materialHandle) || GetHandleIndex(materialHandle) >= WOLF_MAX_MATERIAL_COUNT)
    {
        materialHandle = FALLBACK_HANDLE;
        InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_FALLBACK_SUBSTITUTE], 1);
    }

    GpuInstanceData instanceData;
    instanceData.world = update.world;
    instanceData.boundsCenterRadius = update.boundsCenterRadius;
    instanceData.materialHandle = materialHandle;
    instanceData.meshHandle = meshHandle;
    instanceData.flags = update.drawFlags;
    instanceData.pad0 = 0;
    g_InstanceTable[instanceIndex] = instanceData;

    g_DrawGenerations[drawIndex] = drawGeneration;
    g_InstanceGenerations[instanceIndex] = instanceGeneration;

    if (update.type == UPDATE_ADD || update.type == UPDATE_MATERIAL)
    {
        uint materialIndex = GetHandleIndex(materialHandle);
        uint materialGeneration = GetHandleGeneration(materialHandle);
        if (materialIndex < WOLF_MAX_MATERIAL_COUNT)
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
            g_MaterialTable[materialIndex] = materialData;
            g_MaterialGenerations[materialIndex] = materialGeneration;
        }
    }

    if (update.type == UPDATE_ADD || update.type == UPDATE_MESH)
    {
        uint meshIndex = GetHandleIndex(meshHandle);
        uint meshGeneration = GetHandleGeneration(meshHandle);
        if (meshIndex < WOLF_MAX_MESH_COUNT)
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
            g_MeshTable[meshIndex] = meshData;
            g_MeshGenerations[meshIndex] = meshGeneration;
        }
    }

    GpuDrawCommand drawCommand;
    drawCommand.instanceHandle = update.instanceHandle;
    drawCommand.drawHandle = update.drawHandle;
    drawCommand.flags = update.drawFlags | DRAW_FLAG_ACTIVE;
    drawCommand.pad0 = 0;
    g_DrawCommands[drawIndex] = drawCommand;
}
