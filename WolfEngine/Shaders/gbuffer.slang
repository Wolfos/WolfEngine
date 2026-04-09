#define WOLF_NO_RW_TEXTURES 1
import "common_bindless.slang";
import "draw_data.slang";
import "normal_decode.slang";

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
    float3 normal : TEXCOORD0;
    float2 uv : TEXCOORD1;
    float4 tangent : TEXCOORD2;
    uint instanceId : TEXCOORD3;
    float4 currentClip : TEXCOORD4;
    float4 previousClip : TEXCOORD5;
};

VSOutput vertexShader(VSInput input)
{
    VSOutput output;
    GpuDrawArgs drawArgs = g_DrawArgsTable[0];
    if (drawArgs.instanceCount == 0 || drawArgs.startInstance == 0 || drawArgs.startInstance >= WOLF_MAX_INSTANCE_COUNT)
    {
        // Emit a guaranteed clipped position for inactive commands.
        output.position = float4(2.0f, 2.0f, 2.0f, 1.0f);
        output.normal = float3(0.0f, 1.0f, 0.0f);
        output.uv = float2(0.0f, 0.0f);
        output.tangent = float4(1.0f, 0.0f, 0.0f, 1.0f);
        output.instanceId = 0;
        output.currentClip = output.position;
        output.previousClip = output.position;
        return output;
    }

    uint instanceId = drawArgs.startInstance;
    GpuInstanceData instanceData = g_InstanceTable[instanceId];
    float4 worldPosition = mul(input.position, instanceData.world);
    worldPosition.xyz -= cameraPosition;
    float4 previousWorldPosition = mul(input.position, instanceData.previousWorld);
    previousWorldPosition.xyz -= previousCameraPosition;
    float4 worldNormal = mul(float4(input.normal, 0.0f), instanceData.world);
    float3 worldTangent = mul(float4(input.tangent.xyz, 0.0f), instanceData.world).xyz;

    output.position = mul(worldPosition, viewProjection);
    output.currentClip = mul(worldPosition, unjitteredViewProjection);
    output.previousClip = mul(previousWorldPosition, previousViewProjection);
    output.normal = normalize(worldNormal.xyz);
    output.uv = input.uv;
    output.tangent = float4(normalize(worldTangent), input.tangent.w);
    output.instanceId = instanceId;
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

PSOutput EvaluateFragment(VSOutput input)
{
    PSOutput output;
    
    if (input.instanceId >= WOLF_MAX_INSTANCE_COUNT)
    {
        output.albedo = float4(1.0f, 0.0f, 1.0f, 1.0f);
        output.normal = float4(0.5f, 0.5f, 1.0f, 1.0f);
        output.material = float4(0.0f, 1.0f, 1.0f, 1.0f);
        output.emissive = float4(0.0f, 0.0f, 0.0f, 1.0f);
        output.velocity = float4(0.0f, 0.0f, 1.0f, 1.0f);
        return output;
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
    float3 normal = normalize(input.normal);

    SamplerState sampler = GetSampler(materialData.samplerHandle);
    Texture2D<float4> albedoTex = GetTexture(materialData.albedoHandle);
    Texture2D<float4> metallicRoughnessTex = GetTexture(materialData.metallicRoughnessHandle);
    Texture2D<float4> normalTex = GetTexture(materialData.normalHandle);
    Texture2D<float4> occlusionTex = GetTexture(materialData.occlusionHandle);
    Texture2D<float4> emissiveTex = GetTexture(materialData.emissiveHandle);

    float4 albedoSample = albedoTex.Sample(sampler, input.uv);
    float4 mrSample = metallicRoughnessTex.Sample(sampler, input.uv);
#ifdef WOLF_ALPHA_CLIP
    if(albedoSample.a < materialData.metallicRoughness.z) discard;
#endif
    float3 tangentNormal = DecodeNormalMapSample(normalTex.Sample(sampler, input.uv));
    float4 occlusionSample = occlusionTex.Sample(sampler, input.uv);
    float4 emissiveSample = emissiveTex.Sample(sampler, input.uv);
    float3 emissive = emissiveSample.rgb * materialData.emissiveFactorIntensity.rgb * materialData.emissiveFactorIntensity.a;
    float3 N = normal;
    float3 T = normalize(input.tangent.xyz);
    float3 B = normalize(cross(N, T) * input.tangent.w);
    float3x3 TBN = float3x3(T, B, N);
    normal = normalize(mul(tangentNormal, TBN));
   
    float metallic = saturate(mrSample.b * materialData.metallicRoughness.x);
    float roughness = saturate(mrSample.g * materialData.metallicRoughness.y);
    float occlusion = saturate(occlusionSample.r);
    float alphaTestMask = 0.0f;
#ifdef WOLF_ALPHA_CLIP
    alphaTestMask = 1.0f;
#endif
    float4 materialParams = float4(metallic, roughness, occlusion, alphaTestMask);

    output.albedo = materialData.baseColor * albedoSample;
    output.normal = float4(normal * 0.5f + 0.5f, 1.0f);
    output.material = materialParams;
    output.emissive = float4(emissive, 1.0f);
    uint2 frameSize = uint2(max(frameSizeX, 1u), max(frameSizeY, 1u));
    float2 currentPixel = ClipToPixel(input.currentClip, frameSize);
    float2 previousPixel = ClipToPixel(input.previousClip, frameSize);
    float previousDepth = ClipToDepth(input.previousClip);
    output.velocity = float4(previousPixel - currentPixel, previousDepth, 1.0f);

    return output;
}

PSOutput fragmentShader(VSOutput input)
{
    return EvaluateFragment(input);
}
