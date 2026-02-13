#define WOLF_NO_RW_TEXTURES 1
import "common_bindless.slang";
import "draw_data.slang";

#define WOLF_MAX_INSTANCE_COUNT 10000
#define WOLF_MAX_MATERIAL_COUNT 4096

StructuredBuffer<GpuInstanceData> g_InstanceTable : register(t10);
StructuredBuffer<GpuMaterialData> g_MaterialTable : register(t11);

cbuffer CameraParams : register(b2)
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
    float3 normal : TEXCOORD0;
    float2 uv : TEXCOORD1;
    float4 tangent : TEXCOORD2;
    uint instanceId : TEXCOORD3;
};

VSOutput vertexShader(VSInput input)
{
    VSOutput output;
    if (input.instanceId >= WOLF_MAX_INSTANCE_COUNT)
    {
        output.position = float4(0.0f, 0.0f, 0.0f, 0.0f);
        output.normal = float3(0.0f, 1.0f, 0.0f);
        output.uv = float2(0.0f, 0.0f);
        output.tangent = float4(1.0f, 0.0f, 0.0f, 1.0f);
        output.instanceId = 0;
        return output;
    }
    GpuInstanceData instanceData = g_InstanceTable[input.instanceId];
    float4 worldPosition = mul(input.position, instanceData.world);
    worldPosition.xyz -= cameraPosition;
    float4 worldNormal = mul(float4(input.normal, 0.0f), instanceData.world);
    float3 worldTangent = mul(float4(input.tangent.xyz, 0.0f), instanceData.world).xyz;

    output.position = mul(worldPosition, viewProjection);
    output.normal = normalize(worldNormal.xyz);
    output.uv = input.uv;
    output.tangent = float4(normalize(worldTangent), input.tangent.w);
    output.instanceId = input.instanceId;
    return output;
}

struct PSOutput
{
    float4 albedo : SV_Target0;
    float4 normal : SV_Target1;
    float4 material : SV_Target2;
    float4 emissive : SV_Target3;
};

PSOutput fragmentShader(VSOutput input)
{
    PSOutput output;
    
    if (input.instanceId >= WOLF_MAX_INSTANCE_COUNT)
    {
        output.albedo = float4(1.0f, 0.0f, 1.0f, 1.0f);
        output.normal = float4(0.5f, 0.5f, 1.0f, 1.0f);
        output.material = float4(0.0f, 1.0f, 1.0f, 1.0f);
        output.emissive = float4(0.0f, 0.0f, 0.0f, 1.0f);
        return output;
    }

    GpuInstanceData instanceData = g_InstanceTable[input.instanceId];
    if (instanceData.materialId >= WOLF_MAX_MATERIAL_COUNT)
    {
        output.albedo = float4(1.0f, 0.0f, 1.0f, 1.0f);
        output.normal = float4(0.5f, 0.5f, 1.0f, 1.0f);
        output.material = float4(0.0f, 1.0f, 1.0f, 1.0f);
        output.emissive = float4(0.0f, 0.0f, 0.0f, 1.0f);
        return output;
    }

    GpuMaterialData materialData = g_MaterialTable[instanceData.materialId];
    float3 normal = normalize(input.normal);

    SamplerState sampler = GetSampler(materialData.samplerHandle);
    Texture2D<float4> albedoTex = GetTexture(materialData.albedoHandle);
    Texture2D<float4> metallicRoughnessTex = GetTexture(materialData.metallicRoughnessHandle);
    Texture2D<float4> normalTex = GetTexture(materialData.normalHandle);
    Texture2D<float4> occlusionTex = GetTexture(materialData.occlusionHandle);
    Texture2D<float4> emissiveTex = GetTexture(materialData.emissiveHandle);

    float4 albedoSample = albedoTex.Sample(sampler, input.uv);
    float3 tangentNormal = normalize(normalTex.Sample(sampler, input.uv).rgb * 2.0f - 1.0f);
    float4 mrSample = metallicRoughnessTex.Sample(sampler, input.uv);
    float4 occlusionSample = occlusionTex.Sample(sampler, input.uv);
    float4 emissiveSample = emissiveTex.Sample(sampler, input.uv);
    float3 N = normal;
    float3 T = normalize(input.tangent.xyz);
    float3 B = normalize(cross(N, T) * input.tangent.w);
    float3x3 TBN = float3x3(T, B, N);
    normal = normalize(mul(tangentNormal, TBN));
   
    float metallic = saturate(mrSample.b * materialData.metallicRoughness.x);
    float roughness = saturate(mrSample.g * materialData.metallicRoughness.y);
    float occlusion = saturate(occlusionSample.r);
    float4 materialParams = float4(metallic, roughness, occlusion, 1.0f);

    output.albedo = materialData.baseColor * albedoSample;
    output.normal = float4(normal * 0.5f + 0.5f, 1.0f);
    output.material = materialParams;
    output.emissive = emissiveSample;

    return output;
}
