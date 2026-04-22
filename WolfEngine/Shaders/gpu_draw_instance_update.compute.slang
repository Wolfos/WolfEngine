import "draw_data.slang";

#define WOLF_MAX_DRAW_COUNT 65535
#define WOLF_MAX_INSTANCE_COUNT 65535
#define WOLF_MAX_MATERIAL_COUNT 65535
#define WOLF_MAX_MESH_COUNT 65535

static const uint UPDATE_REMOVE = 1;
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

RWStructuredBuffer<GpuDrawInstanceUpdateData> g_Updates : register(u0);
RWStructuredBuffer<GpuInstanceData> g_InstanceTable : register(u1);
RWStructuredBuffer<GpuDrawCommand> g_DrawCommands : register(u2);
RWStructuredBuffer<uint> g_DrawGenerations : register(u3);
RWStructuredBuffer<uint> g_InstanceGenerations : register(u4);
RWStructuredBuffer<uint> g_Diagnostics : register(u5);

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
void CSUpdateInstance(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint index = dispatchThreadId.x;
    if (index >= updateCount)
    {
        return;
    }

    GpuDrawInstanceUpdateData update = g_Updates[index];
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

    GpuDrawCommand drawCommand = (GpuDrawCommand)0;
    drawCommand.instanceHandle = update.instanceHandle;
    drawCommand.drawHandle = update.drawHandle;
    drawCommand.flags = update.drawFlags;
    drawCommand.pad0 = 0;
    g_DrawCommands[drawIndex] = drawCommand;
}
