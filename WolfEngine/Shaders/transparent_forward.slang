#define WOLF_NO_RW_TEXTURES 1
import "common_bindless.slang";
import "draw_data.slang";
import "common_lighting.slang";

#define WOLF_MAX_INSTANCE_COUNT 20000
#define WOLF_MAX_MATERIAL_COUNT 8192

StructuredBuffer<GpuInstanceData> g_InstanceTable : register(t10);
StructuredBuffer<GpuMaterialData> g_MaterialTable : register(t11);
StructuredBuffer<GpuDrawArgs> g_DrawArgsTable : register(t12);
StructuredBuffer<uint> g_MaterialGenerations : register(t13);

cbuffer TransparentEnvironmentParams : register(b0)
{
    uint environmentHandle;
    uint irradianceHandle;
    uint prefilteredHandle;
    uint brdfLutHandle;
    uint samplerHandle;
    uint shadowMapHandle0;
    uint shadowMapHandle1;
    uint shadowMapHandle2;
    uint shadowSamplerHandle;
    uint _envPadding0;
    uint _envPadding1;
    uint _envPadding2;
};

cbuffer CameraParams : register(b2)
{
    row_major float4x4 viewProjection;
    float3 cameraPosition;
    float padding;
};

cbuffer LightingParams : register(b3)
{
    uint lightCount;
    uint3 _lightingPadding;
    LightData lights[MAX_LIGHTS];
    row_major float4x4 shadowViewProjection0;
    row_major float4x4 shadowViewProjection1;
    row_major float4x4 shadowViewProjection2;
    float4 shadowSplitsBlend;
    float4 shadowTexelSizeBiasStrength;
    uint shadowLightIndex;
    uint shadowsEnabled;
    float shadowMaxDistance;
    uint _shadowPadding1;
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
    float3 worldPos : TEXCOORD0;
    float3 normal : TEXCOORD1;
    float2 uv : TEXCOORD2;
    float4 tangent : TEXCOORD3;
    uint instanceId : TEXCOORD4;
};

VSOutput vertexShader(VSInput input)
{
    VSOutput output;
    GpuDrawArgs drawArgs = g_DrawArgsTable[0];
    if (drawArgs.instanceCount == 0 || drawArgs.startInstance == 0 || drawArgs.startInstance >= WOLF_MAX_INSTANCE_COUNT)
    {
        output.position = float4(2.0f, 2.0f, 2.0f, 1.0f);
        output.worldPos = float3(0.0f, 0.0f, 0.0f);
        output.normal = float3(0.0f, 1.0f, 0.0f);
        output.uv = float2(0.0f, 0.0f);
        output.tangent = float4(1.0f, 0.0f, 0.0f, 1.0f);
        output.instanceId = 0;
        return output;
    }

    uint instanceId = drawArgs.startInstance;
    GpuInstanceData instanceData = g_InstanceTable[instanceId];

    float4 worldPosition = mul(input.position, instanceData.world);
    float3 cameraRelativeWorldPosition = worldPosition.xyz - cameraPosition;
    float4 worldNormal = mul(float4(input.normal, 0.0f), instanceData.world);
    float3 worldTangent = mul(float4(input.tangent.xyz, 0.0f), instanceData.world).xyz;

    output.position = mul(float4(cameraRelativeWorldPosition, 1.0f), viewProjection);
    output.worldPos = cameraRelativeWorldPosition;
    output.normal = normalize(worldNormal.xyz);
    output.uv = input.uv;
    output.tangent = float4(normalize(worldTangent), input.tangent.w);
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
    float3 tangentNormal = normalize(normalTex.Sample(sampler, input.uv).rgb * 2.0f - 1.0f);
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

    SurfaceLightingInput surface;
    surface.worldPos = input.worldPos;
    surface.normal = normal;
    surface.albedo = (materialData.baseColor * albedoSample).rgb;
    surface.metallic = metallic;
    surface.roughness = roughness;
    surface.occlusion = occlusion;
    surface.emissive = emissive;

    float3 viewDir = normalize(-input.worldPos);
    float3 color = EvaluateSurfaceLighting(
        surface,
        viewDir,
        lightCount,
        lights,
        shadowViewProjection0,
        shadowViewProjection1,
        shadowViewProjection2,
        shadowMapHandle0,
        shadowMapHandle1,
        shadowMapHandle2,
        shadowSamplerHandle,
        shadowSplitsBlend.xyz,
        shadowSplitsBlend.w,
        shadowLightIndex,
        shadowTexelSizeBiasStrength.xy,
        shadowTexelSizeBiasStrength.z,
        shadowTexelSizeBiasStrength.w,
        shadowsEnabled,
        shadowMaxDistance,
        irradianceHandle,
        prefilteredHandle,
        brdfLutHandle,
        samplerHandle,
        IBL_EXPOSURE);

    float alpha = saturate(materialData.baseColor.a * albedoSample.a);
    return float4(color, alpha);
}
