import "draw_data.slang";

#define WOLF_MAX_MATERIAL_COUNT 65535

static const uint HARDEN_COUNTER_STALE_REJECT = 0;

cbuffer UpdateParams : register(b11)
{
    uint updateCount;
    uint pad0;
    uint pad1;
    uint pad2;
};

RWStructuredBuffer<GpuTerrainMaterialUpdateData> g_Updates : register(u0);
RWStructuredBuffer<GpuTerrainMaterialData> g_TerrainMaterialTable : register(u1);
RWStructuredBuffer<uint> g_MaterialGenerations : register(u2);
RWStructuredBuffer<uint> g_Diagnostics : register(u3);

[numthreads(64, 1, 1)]
void CSUpdateTerrainMaterial(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint index = dispatchThreadId.x;
    if (index >= updateCount)
    {
        return;
    }

    GpuTerrainMaterialUpdateData update = g_Updates[index];
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

    GpuTerrainMaterialData terrainData = (GpuTerrainMaterialData)0;
    terrainData.heightmapHandle = update.heightmapHandle;
    terrainData.layerIndexMapHandle = update.layerIndexMapHandle;
    terrainData.layerWeightMapHandle = update.layerWeightMapHandle;
    terrainData.hasLayerMaps = update.hasLayerMaps;
    terrainData.heightmapSamplerHandle = update.heightmapSamplerHandle;
    terrainData.layerSamplerHandle = update.layerSamplerHandle;
    terrainData.layerMapSamplerHandle = update.layerMapSamplerHandle;
    terrainData.layerStart = update.layerStart;
    terrainData.layerCount = update.layerCount;
    terrainData.heightBlendSharpness = update.heightBlendSharpness;
    terrainData.heightScale = update.heightScale;
    terrainData.autoMaterialBlendDegrees = update.autoMaterialBlendDegrees;
    g_TerrainMaterialTable[materialIndex] = terrainData;
    g_MaterialGenerations[materialIndex] = materialGeneration;
}
