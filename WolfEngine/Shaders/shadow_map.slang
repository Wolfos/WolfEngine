#define WOLF_NO_RW_TEXTURES 1
import "common_bindless.slang";
import "draw_data.slang";

#define WOLF_MAX_INSTANCE_COUNT 20000
#define WOLF_MAX_MATERIAL_COUNT 8192

StructuredBuffer<GpuInstanceData> g_InstanceTable : register(t10);
StructuredBuffer<GpuMaterialData> g_MaterialTable : register(t11);
StructuredBuffer<GpuDrawArgs> g_DrawArgsTable : register(t12);
StructuredBuffer<uint> g_MaterialGenerations : register(t13);

cbuffer CameraParams : register(b14)
{
    row_major float4x4 viewProjection;
    float3 cameraPosition;
    float padding;
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
    float2 uv : TEXCOORD0;
    uint instanceId : TEXCOORD1;
};

VSOutput vertexShader(VSInput input)
{
    VSOutput output;
    GpuDrawArgs drawArgs = g_DrawArgsTable[0];
    if (drawArgs.instanceCount == 0 || drawArgs.startInstance == 0 || drawArgs.startInstance >= WOLF_MAX_INSTANCE_COUNT)
    {
        output.position = float4(2.0f, 2.0f, 2.0f, 1.0f);
        output.uv = float2(0.0f, 0.0f);
        output.instanceId = 0;
        return output;
    }

    uint instanceId = drawArgs.startInstance;
    GpuInstanceData instanceData = g_InstanceTable[instanceId];
    float4 worldPosition = mul(input.position, instanceData.world);
    worldPosition.xyz -= cameraPosition;

    output.position = mul(worldPosition, viewProjection);
    output.uv = input.uv;
    output.instanceId = instanceId;
    return output;
}

void fragmentShader(VSOutput input)
{
#ifdef WOLF_ALPHA_CLIP
    if (input.instanceId >= WOLF_MAX_INSTANCE_COUNT)
    {
        discard;
    }

    GpuInstanceData instanceData = g_InstanceTable[input.instanceId];
    uint materialIndex = 0;
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

    GpuMaterialData materialData = g_MaterialTable[materialIndex];
    SamplerState sampler = GetSampler(materialData.samplerHandle);
    Texture2D<float4> albedoTex = GetTexture(materialData.albedoHandle);
    float alpha = albedoTex.Sample(sampler, input.uv).a * materialData.baseColor.a;
    if (alpha < materialData.metallicRoughness.z)
    {
        discard;
    }
#endif
}
