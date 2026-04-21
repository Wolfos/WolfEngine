#define WOLF_NO_RW_TEXTURES 1
import "common_bindless.slang";
import "draw_data.slang";

#define WOLF_MAX_INSTANCE_COUNT 65535
#define WOLF_MAX_MATERIAL_COUNT 65535

StructuredBuffer<GpuInstanceData> g_InstanceTable : register(t10);
StructuredBuffer<GpuMaterialData> g_MaterialTable : register(t11);
StructuredBuffer<GpuDrawArgs> g_DrawArgsTable : register(t12);
StructuredBuffer<uint> g_MaterialGenerations : register(t13);

cbuffer CameraParams : register(b2)
{
    row_major float4x4 viewProjection;
    float3 cameraPosition;
    float currentJitterPixelsX;
    float3 previousCameraPosition;
    float currentJitterPixelsY;
    row_major float4x4 unjitteredViewProjection;
    row_major float4x4 previousViewProjection;
    float2 currentJitterNdc;
    uint frameSizeX;
    uint frameSizeY;
    row_major float4x4 viewMatrix;
    float nearPlane;
    float farPlane;
    float2 _cameraPadding;
};

struct VSInput
{
    float4 position : POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
    float4 tangent : TANGENT;
    uint instanceId : SV_InstanceID;
};

struct VSOutput
{
    float4 position : SV_Position;
    uint instanceId : TEXCOORD0;
};

VSOutput vertexShader(VSInput input)
{
    VSOutput output;
    GpuDrawArgs drawArgs = g_DrawArgsTable[0];
    if (drawArgs.instanceCount == 0 || drawArgs.startInstance == 0 || drawArgs.startInstance >= WOLF_MAX_INSTANCE_COUNT)
    {
        output.position = float4(2.0f, 2.0f, 2.0f, 1.0f);
        output.instanceId = 0u;
        return output;
    }

    uint instanceId = drawArgs.startInstance;
    GpuInstanceData instanceData = g_InstanceTable[instanceId];
    float4 worldPosition = mul(input.position, instanceData.world);
    worldPosition.xyz -= cameraPosition;

    output.position = mul(worldPosition, viewProjection);
    output.instanceId = instanceId;
    return output;
}

float4 fragmentShader(VSOutput input) : SV_Target0
{
    if (input.instanceId >= WOLF_MAX_INSTANCE_COUNT)
    {
        return float4(1.0f, 0.0f, 1.0f, 1.0f);
    }

    GpuInstanceData instanceData = g_InstanceTable[input.instanceId];
    uint materialIndex = 0u;
    uint materialHandle = instanceData.materialHandle;
    if (IsPackedHandleValid(materialHandle))
    {
        uint candidateIndex = GetHandleIndex(materialHandle);
        uint candidateGeneration = GetHandleGeneration(materialHandle);
        if (candidateIndex < WOLF_MAX_MATERIAL_COUNT &&
            g_MaterialGenerations[candidateIndex] == candidateGeneration)
        {
            materialIndex = candidateIndex;
        }
    }

    return g_MaterialTable[materialIndex].baseColor;
}
