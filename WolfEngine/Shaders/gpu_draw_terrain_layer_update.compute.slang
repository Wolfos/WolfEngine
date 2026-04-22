import "draw_data.slang";

#define WOLF_MAX_MATERIAL_COUNT 65535
#define WOLF_MAX_TERRAIN_LAYER_COUNT 1048560

static const uint HARDEN_COUNTER_STALE_REJECT = 0;

cbuffer UpdateParams : register(b11)
{
    uint updateCount;
    uint pad0;
    uint pad1;
    uint pad2;
};

RWStructuredBuffer<GpuTerrainLayerUpdateData> g_Updates : register(u0);
RWStructuredBuffer<GpuTerrainMaterialData> g_TerrainMaterialTable : register(u1);
RWStructuredBuffer<GpuTerrainLayerData> g_TerrainLayerTable : register(u2);
RWStructuredBuffer<uint> g_MaterialGenerations : register(u3);
RWStructuredBuffer<uint> g_Diagnostics : register(u4);

[numthreads(64, 1, 1)]
void CSUpdateTerrainLayer(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint index = dispatchThreadId.x;
    if (index >= updateCount)
    {
        return;
    }

    GpuTerrainLayerUpdateData update = g_Updates[index];
    if (!IsPackedHandleValid(update.materialHandle))
    {
        InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_STALE_REJECT], 1);
        return;
    }

    uint materialIndex = GetHandleIndex(update.materialHandle);
    uint materialGeneration = GetHandleGeneration(update.materialHandle);
    if (materialIndex == 0 || materialIndex >= WOLF_MAX_MATERIAL_COUNT)
    {
        InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_STALE_REJECT], 1);
        return;
    }

    if (g_MaterialGenerations[materialIndex] != materialGeneration)
    {
        InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_STALE_REJECT], 1);
        return;
    }

    GpuTerrainMaterialData terrainData = g_TerrainMaterialTable[materialIndex];
    uint layerTableIndex = terrainData.layerStart + update.layerIndex;
    if (layerTableIndex >= WOLF_MAX_TERRAIN_LAYER_COUNT)
    {
        InterlockedAdd(g_Diagnostics[HARDEN_COUNTER_STALE_REJECT], 1);
        return;
    }

    GpuTerrainLayerData layerData = (GpuTerrainLayerData)0;
    layerData.albedoHandle = update.albedoHandle;
    layerData.normalHandle = update.normalHandle;
    layerData.metallicRoughnessHandle = update.metallicRoughnessHandle;
    layerData.occlusionHandle = update.occlusionHandle;
    layerData.heightHandle = update.heightHandle;
    layerData.hasHeight = update.hasHeight;
    layerData.scale = update.scale;
    g_TerrainLayerTable[layerTableIndex] = layerData;
}
