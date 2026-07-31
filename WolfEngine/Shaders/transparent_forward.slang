#define WOLF_NO_RW_TEXTURES 1
import "common_bindless.slang";
import "draw_data.slang";
import "common_lighting.slang";
import "normal_decode.slang";

#define WOLF_MAX_INSTANCE_COUNT 65535
#define WOLF_MAX_MATERIAL_COUNT 65535

StructuredBuffer<GpuInstanceData> g_InstanceTable : register(t10);
StructuredBuffer<GpuMaterialData> g_MaterialTable : register(t11);
StructuredBuffer<GpuDrawArgs> g_DrawArgsTable : register(t12);
StructuredBuffer<uint> g_MaterialGenerations : register(t13);
StructuredBuffer<PointLightData> g_PointLights : register(t14);
StructuredBuffer<ClusterHeader> g_ClusterHeaders : register(t15);
StructuredBuffer<uint> g_ClusterLightIndices : register(t16);

// Written per indirect command by the command signature's root constant rather than by any binding
// call, so each draw can find its own entry in the draw args table.
cbuffer DrawIndexParams : register(b1)
{
    uint drawIndex;
};

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

cbuffer LightingParams : register(b3)
{
    uint directionalLightCount;
    uint shadowLightIndex;
    uint shadowsEnabled;
    uint _lightingPadding0;
    DirectionalLightData directionalLights[MAX_DIRECTIONAL_LIGHTS];
    row_major float4x4 shadowViewProjection0;
    row_major float4x4 shadowViewProjection1;
    row_major float4x4 shadowViewProjection2;
    float4 shadowSplitsBlend;
    float4 shadowTexelSizeBiasStrength;
    float4 shadowDepthBiases;
    float shadowMaxDistance;
    float _lightingPadding1;
    float _lightingPadding2;
    float _lightingPadding3;
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
    float3 worldPos : TEXCOORD0;
    float3 viewPos : TEXCOORD1;
    float3 normal : TEXCOORD2;
    float2 uv : TEXCOORD3;
    float4 tangent : TEXCOORD4;
    uint instanceId : TEXCOORD5;
};

VSOutput vertexShader(VSInput input)
{
    VSOutput output;
    GpuDrawArgs drawArgs = g_DrawArgsTable[drawIndex];
    if (drawArgs.instanceCount == 0 || drawArgs.startInstance == 0 || drawArgs.startInstance >= WOLF_MAX_INSTANCE_COUNT)
    {
        output.position = float4(2.0f, 2.0f, 2.0f, 1.0f);
        output.worldPos = 0.0f.xxx;
        output.viewPos = 0.0f.xxx;
        output.normal = float3(0.0f, 1.0f, 0.0f);
        output.uv = 0.0f.xx;
        output.tangent = float4(1.0f, 0.0f, 0.0f, 1.0f);
        output.instanceId = 0u;
        return output;
    }

    uint instanceId = drawArgs.startInstance;
    GpuInstanceData instanceData = g_InstanceTable[instanceId];

    float4 worldPosition = mul(float4(input.position, 1.0f), instanceData.world);
    worldPosition.xyz -= cameraPosition;
    float4 viewPosition = mul(float4(worldPosition.xyz, 1.0f), viewMatrix);
    float4 worldNormal = mul(float4(input.normal, 0.0f), instanceData.world);
    float3 worldTangent = mul(float4(input.tangent.xyz, 0.0f), instanceData.world).xyz;

    output.position = mul(float4(worldPosition.xyz, 1.0f), viewProjection);
    output.worldPos = worldPosition.xyz;
    output.viewPos = viewPosition.xyz;
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
    float3 normal = normalize(input.normal);

    SamplerState sampler = GetSampler(materialData.samplerHandle);
    Texture2D<float4> albedoTex = GetTexture(materialData.albedoHandle);
    Texture2D<float4> ormTex = GetTexture(materialData.ormHandle);
    Texture2D<float4> normalTex = GetTexture(materialData.normalHandle);
    Texture2D<float4> emissiveTex = GetTexture(materialData.emissiveHandle);

    float4 albedoSample = albedoTex.Sample(sampler, input.uv);
    float4 ormSample = ormTex.Sample(sampler, input.uv);
    float3 tangentNormal = ApplyNormalMapScale(
        DecodeNormalMapSample(normalTex.Sample(sampler, input.uv)),
        materialData.metallicRoughness.w);
    float4 emissiveSample = emissiveTex.Sample(sampler, input.uv);
    float3 emissive = emissiveSample.rgb * materialData.emissiveFactorIntensity.rgb * materialData.emissiveFactorIntensity.a;

    float3 N = normal;
    float3 T = normalize(input.tangent.xyz);
    float3 B = normalize(cross(N, T) * input.tangent.w);
    float3x3 TBN = float3x3(T, B, N);
    normal = normalize(mul(tangentNormal, TBN));

    SurfaceLightingInput surface;
    surface.worldPos = input.worldPos;
    surface.normal = normal;
    surface.albedo = (materialData.baseColor * albedoSample).rgb;
    surface.occlusion = saturate(ormSample.r);
    surface.roughness = saturate(ormSample.g * materialData.metallicRoughness.y);
    surface.metallic = saturate(ormSample.b * materialData.metallicRoughness.x);
    surface.emissive = emissive;

    float3 viewDir = normalize(-input.worldPos);
    float3 color = EvaluateDirectionalLights(
        surface,
        viewDir,
        directionalLightCount,
        directionalLights,
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
        shadowDepthBiases.xyz,
        shadowTexelSizeBiasStrength.w,
        shadowsEnabled,
        shadowMaxDistance);

    uint2 pixel = uint2(input.position.xy);
    uint clusterIndex = ComputeClusterIndex(pixel, uint2(frameSizeX, frameSizeY), max(input.viewPos.z, nearPlane), nearPlane, farPlane);
    ClusterHeader header = g_ClusterHeaders[clusterIndex];

    uint pointLightIterations = min(header.count, CLUSTER_MAX_LIGHTS_PER_CLUSTER);
    for (uint i = 0u; i < pointLightIterations; ++i)
    {
        uint pointLightIndex = g_ClusterLightIndices[header.offset + i];
        color += EvaluatePointLight(surface, viewDir, g_PointLights[pointLightIndex]);
    }

    color += EvaluateImageBasedLighting(
        surface,
        viewDir,
        irradianceHandle,
        prefilteredHandle,
        brdfLutHandle,
        samplerHandle,
        IBL_EXPOSURE);
    color += surface.emissive;

    float alpha = saturate(materialData.baseColor.a * albedoSample.a);
    return float4(color, alpha);
}
