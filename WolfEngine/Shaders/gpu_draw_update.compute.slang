import "draw_data.slang";

#define WOLF_MAX_DRAW_COUNT 65535
#define WOLF_MAX_INSTANCE_COUNT 65535
#define WOLF_MAX_MATERIAL_COUNT 65535
#define WOLF_MAX_MESH_COUNT 65535

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
RWStructuredBuffer<GpuTerrainMaterialData> g_TerrainMaterialTable : register(u3);
RWStructuredBuffer<GpuMeshData> g_MeshTable : register(u4);
RWStructuredBuffer<GpuDrawCommand> g_DrawCommands : register(u5);
RWStructuredBuffer<uint> g_DrawGenerations : register(u6);
RWStructuredBuffer<uint> g_InstanceGenerations : register(u7);
RWStructuredBuffer<uint> g_MeshGenerations : register(u8);
RWStructuredBuffer<uint> g_MaterialGenerations : register(u9);
RWStructuredBuffer<uint> g_Diagnostics : register(u10);

uint NextGeneration(uint generation)
{
    generation = generation + 1;
    if (generation == 0)
    {
        generation = 1;
    }
    return generation;
}

[numthreads(64, 1, 1)]
void CSUpdate(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint index = dispatchThreadId.x;
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

    GpuInstanceData instanceData = (GpuInstanceData)0;
    instanceData.previousWorld = update.previousWorld;
    instanceData.world = update.world;
    instanceData.boundsCenterRadius = update.boundsCenterRadius;
    instanceData.materialHandle = materialHandle;
    instanceData.meshHandle = meshHandle;
    instanceData.drawKind = update.drawKind;
    instanceData.flags = update.drawFlags;
    g_InstanceTable[instanceIndex] = instanceData;

    g_DrawGenerations[drawIndex] = drawGeneration;
    g_InstanceGenerations[instanceIndex] = instanceGeneration;

    if (update.type == UPDATE_ADD || update.type == UPDATE_MATERIAL)
    {
        uint materialIndex = GetHandleIndex(materialHandle);
        uint materialGeneration = GetHandleGeneration(materialHandle);
        if (materialIndex < WOLF_MAX_MATERIAL_COUNT)
        {
            GpuMaterialData materialData = (GpuMaterialData)0;
            materialData.baseColor = update.baseColor;
            materialData.metallicRoughness = update.metallicRoughness;
            materialData.emissiveFactorIntensity = update.emissiveFactorIntensity;
            materialData.albedoHandle = update.albedoHandle;
            materialData.ormHandle = update.ormHandle;
            materialData.normalHandle = update.normalHandle;
            materialData.emissiveHandle = update.emissiveHandle;
            materialData.samplerHandle = update.samplerHandle;
            materialData.pad0 = 0;
            materialData.pad1 = 0;
            g_MaterialTable[materialIndex] = materialData;

            GpuTerrainMaterialData terrainMaterialData = (GpuTerrainMaterialData)0;
            terrainMaterialData.controlMapHandle = update.controlMapHandle;
            terrainMaterialData.hasControlMap = update.hasControlMap;
            terrainMaterialData.layerSamplerHandle = update.layerSamplerHandle;
            terrainMaterialData.controlSamplerHandle = update.controlSamplerHandle;
            terrainMaterialData.layerCount = update.layerCount;
            terrainMaterialData.heightBlendSharpness = update.heightBlendSharpness;
            terrainMaterialData.pad0 = 0;
            terrainMaterialData.pad1 = 0;
            terrainMaterialData.layer0AlbedoHandle = update.layer0AlbedoHandle;
            terrainMaterialData.layer0NormalHandle = update.layer0NormalHandle;
            terrainMaterialData.layer0MetallicRoughnessHandle = update.layer0MetallicRoughnessHandle;
            terrainMaterialData.layer0OcclusionHandle = update.layer0OcclusionHandle;
            terrainMaterialData.layer0HeightHandle = update.layer0HeightHandle;
            terrainMaterialData.layer0HasHeight = update.layer0HasHeight;
            terrainMaterialData.layer0Scale = update.layer0Scale;
            terrainMaterialData.layer0Pad = 0;
            terrainMaterialData.layer1AlbedoHandle = update.layer1AlbedoHandle;
            terrainMaterialData.layer1NormalHandle = update.layer1NormalHandle;
            terrainMaterialData.layer1MetallicRoughnessHandle = update.layer1MetallicRoughnessHandle;
            terrainMaterialData.layer1OcclusionHandle = update.layer1OcclusionHandle;
            terrainMaterialData.layer1HeightHandle = update.layer1HeightHandle;
            terrainMaterialData.layer1HasHeight = update.layer1HasHeight;
            terrainMaterialData.layer1Scale = update.layer1Scale;
            terrainMaterialData.layer1Pad = 0;
            terrainMaterialData.layer2AlbedoHandle = update.layer2AlbedoHandle;
            terrainMaterialData.layer2NormalHandle = update.layer2NormalHandle;
            terrainMaterialData.layer2MetallicRoughnessHandle = update.layer2MetallicRoughnessHandle;
            terrainMaterialData.layer2OcclusionHandle = update.layer2OcclusionHandle;
            terrainMaterialData.layer2HeightHandle = update.layer2HeightHandle;
            terrainMaterialData.layer2HasHeight = update.layer2HasHeight;
            terrainMaterialData.layer2Scale = update.layer2Scale;
            terrainMaterialData.layer2Pad = 0;
            terrainMaterialData.layer3AlbedoHandle = update.layer3AlbedoHandle;
            terrainMaterialData.layer3NormalHandle = update.layer3NormalHandle;
            terrainMaterialData.layer3MetallicRoughnessHandle = update.layer3MetallicRoughnessHandle;
            terrainMaterialData.layer3OcclusionHandle = update.layer3OcclusionHandle;
            terrainMaterialData.layer3HeightHandle = update.layer3HeightHandle;
            terrainMaterialData.layer3HasHeight = update.layer3HasHeight;
            terrainMaterialData.layer3Scale = update.layer3Scale;
            terrainMaterialData.layer3Pad = 0;
            g_TerrainMaterialTable[materialIndex] = terrainMaterialData;
            g_MaterialGenerations[materialIndex] = materialGeneration;
        }
    }

    if (update.type == UPDATE_ADD || update.type == UPDATE_MESH)
    {
        uint meshIndex = GetHandleIndex(meshHandle);
        uint meshGeneration = GetHandleGeneration(meshHandle);
        if (meshIndex < WOLF_MAX_MESH_COUNT)
        {
            GpuMeshData meshData = (GpuMeshData)0;
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

    GpuDrawCommand drawCommand = (GpuDrawCommand)0;
    drawCommand.instanceHandle = update.instanceHandle;
    drawCommand.drawHandle = update.drawHandle;
    drawCommand.flags = update.drawFlags;
    drawCommand.pad0 = 0;
    g_DrawCommands[drawIndex] = drawCommand;
}
