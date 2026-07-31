#define WOLF_NO_RW_TEXTURES 1
import "common_bindless.slang";
import "draw_data.slang";

#define WOLF_MAX_INSTANCE_COUNT 65535
#define WOLF_MAX_MATERIAL_COUNT 65535

StructuredBuffer<GpuInstanceData> g_InstanceTable : register(t10);
StructuredBuffer<GpuMaterialData> g_MaterialTable : register(t11);
StructuredBuffer<GpuDrawArgs> g_DrawArgsTable : register(t12);
StructuredBuffer<uint> g_MaterialGenerations : register(t13);

// Written per indirect command by the command signature's root constant rather than by any binding
// call, so each draw can find its own entry in the draw args table.
cbuffer DrawIndexParams : register(b1)
{
    uint drawIndex;
};

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
    float3 position : POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
    float4 tangent : TANGENT;
    uint instanceId : SV_InstanceID;
};

struct VSOutput
{
    float4 position : SV_Position;
    float3 normal : TEXCOORD0;
    uint instanceId : TEXCOORD1;
    float4 currentClip : TEXCOORD2;
    float4 previousClip : TEXCOORD3;
};

VSOutput vertexShader(VSInput input)
{
    VSOutput output;
    GpuDrawArgs drawArgs = g_DrawArgsTable[drawIndex];
    if (drawArgs.instanceCount == 0 || drawArgs.startInstance == 0 || drawArgs.startInstance >= WOLF_MAX_INSTANCE_COUNT)
    {
        output.position = float4(2.0f, 2.0f, 2.0f, 1.0f);
        output.normal = float3(0.0f, 1.0f, 0.0f);
        output.instanceId = 0u;
        output.currentClip = output.position;
        output.previousClip = output.position;
        return output;
    }

    uint instanceId = drawArgs.startInstance;
    GpuInstanceData instanceData = g_InstanceTable[instanceId];
    float4 worldPosition = mul(float4(input.position, 1.0f), instanceData.world);
    worldPosition.xyz -= cameraPosition;
    float4 previousWorldPosition = mul(float4(input.position, 1.0f), instanceData.previousWorld);
    previousWorldPosition.xyz -= previousCameraPosition;
    float4 worldNormal = mul(float4(input.normal, 0.0f), instanceData.world);

    output.position = mul(worldPosition, viewProjection);
    output.normal = normalize(worldNormal.xyz);
    output.instanceId = instanceId;
    output.currentClip = mul(worldPosition, unjitteredViewProjection);
    output.previousClip = mul(previousWorldPosition, previousViewProjection);
    return output;
}

struct PSOutput
{
    float4 albedo : SV_Target0;
    float4 normal : SV_Target1;
    float4 material : SV_Target2;
    float4 emissive : SV_Target3;
    float4 velocity : SV_Target4;
};

float2 ClipToPixel(float4 clipPosition, uint2 frameSize)
{
    float invW = abs(clipPosition.w) > 1e-5f ? rcp(clipPosition.w) : 0.0f;
    float2 ndc = clipPosition.xy * invW;
    return float2(
        (ndc.x * 0.5f + 0.5f) * frameSize.x,
        (-ndc.y * 0.5f + 0.5f) * frameSize.y);
}

float ClipToDepth(float4 clipPosition)
{
    float invW = abs(clipPosition.w) > 1e-5f ? rcp(clipPosition.w) : 0.0f;
    return clipPosition.z * invW;
}

PSOutput fragmentShader(VSOutput input)
{
    PSOutput output;
    if (input.instanceId >= WOLF_MAX_INSTANCE_COUNT)
    {
        output.albedo = 0.0f.xxxx;
        output.normal = float4(0.5f, 0.5f, 1.0f, 1.0f);
        output.material = 0.0f.xxxx;
        output.emissive = float4(1.0f, 0.0f, 1.0f, 1.0f);
        output.velocity = 0.0f.xxxx;
        return output;
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

    GpuMaterialData materialData = g_MaterialTable[materialIndex];
    uint2 frameSize = uint2(max(frameSizeX, 1u), max(frameSizeY, 1u));
    float2 currentPixel = ClipToPixel(input.currentClip, frameSize);
    float2 previousPixel = ClipToPixel(input.previousClip, frameSize);
    float previousDepth = ClipToDepth(input.previousClip);

    output.albedo = float4(0.0f, 0.0f, 0.0f, materialData.baseColor.a);
    output.normal = float4(normalize(input.normal) * 0.5f + 0.5f, 1.0f);
    output.material = 0.0f.xxxx;
    output.emissive = float4(materialData.baseColor.rgb, 1.0f);
    output.velocity = float4(previousPixel - currentPixel, previousDepth, 1.0f);
    return output;
}
