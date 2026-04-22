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

RWStructuredBuffer<GpuDrawMaterialUpdateData> g_Updates : register(u0);
RWStructuredBuffer<GpuMaterialData> g_MaterialTable : register(u1);
RWStructuredBuffer<uint> g_MaterialGenerations : register(u2);
RWStructuredBuffer<uint> g_Diagnostics : register(u3);

[numthreads(64, 1, 1)]
void CSUpdateMaterial(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint index = dispatchThreadId.x;
    if (index >= updateCount)
    {
        return;
    }

    GpuDrawMaterialUpdateData update = g_Updates[index];
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

    GpuMaterialData materialData = (GpuMaterialData)0;
    materialData.baseColor = update.baseColor;
    materialData.metallicRoughness = update.metallicRoughness;
    materialData.emissiveFactorIntensity = update.emissiveFactorIntensity;
    materialData.albedoHandle = update.albedoHandle;
    materialData.metallicRoughnessHandle = update.metallicRoughnessHandle;
    materialData.normalHandle = update.normalHandle;
    materialData.occlusionHandle = update.occlusionHandle;
    materialData.emissiveHandle = update.emissiveHandle;
    materialData.samplerHandle = update.samplerHandle;
    g_MaterialTable[materialIndex] = materialData;
    g_MaterialGenerations[materialIndex] = materialGeneration;
}
