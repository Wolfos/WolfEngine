import "common.slang";
import "common_bindless.slang";
import "common_lighting.slang";

cbuffer BindlessHandles : register(b0)
{
    uint gbufferAlbedoHandle;
    uint gbufferNormalHandle;
    uint gbufferMaterialHandle;
    uint gbufferEmissiveHandle;
    uint gbufferDepthHandle;
    uint ambientOcclusionHandle;
    uint environmentHandle;
    uint irradianceHandle;
    uint prefilteredHandle;
    uint brdfLutHandle;
    uint lightingTargetHandle;
    uint samplerHandle;
    uint shadowMapHandle0;
    uint shadowMapHandle1;
    uint shadowMapHandle2;
    uint shadowSamplerHandle;
};

cbuffer CameraParams : register(b1)
{
    CameraParams camera;
};

cbuffer LightingParams : register(b2)
{
    uint directionalLightCount;
    uint shadowLightIndex;
    uint shadowsEnabled;
    uint aoEnabled;
    DirectionalLightData directionalLights[MAX_DIRECTIONAL_LIGHTS];
    row_major float4x4 shadowViewProjection0;
    row_major float4x4 shadowViewProjection1;
    row_major float4x4 shadowViewProjection2;
    float4 shadowSplitsBlend;
    float4 shadowTexelSizeBiasStrength;
    float shadowMaxDistance;
    float nearPlane;
    float farPlane;
    uint framebufferSizeX;
    uint framebufferSizeY;
};

RWStructuredBuffer<PointLightData> g_PointLights : register(u3);
RWStructuredBuffer<ClusterHeader> g_ClusterHeaders : register(u4);
RWStructuredBuffer<uint> g_ClusterLightIndices : register(u5);

float3 ReconstructWorldPosition(uint2 pixel, uint2 size, float depth, float4x4 invViewProjection)
{
    float2 ndc;
    ndc.x = (float(pixel.x) + 0.5f) / float(size.x) * 2.0f - 1.0f;
    ndc.y = -((float(pixel.y) + 0.5f) / float(size.y) * 2.0f - 1.0f);

    float4 clip = float4(ndc, depth, 1.0f);
    float4 worldPosH = mul(clip, invViewProjection);
    return worldPosH.xyz / worldPosH.w;
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint width, height;
    RWTexture2D<float4> lightingTarget = GetRWTexture(lightingTargetHandle);
    lightingTarget.GetDimensions(width, height);
    if (dispatchThreadId.x >= width || dispatchThreadId.y >= height)
    {
        return;
    }

    float2 uv = (float2(dispatchThreadId.xy) + 0.5f) / float2(width, height);

    Texture2D<float4> gbufferAlbedo = GetTexture(gbufferAlbedoHandle);
    Texture2D<float4> gbufferNormal = GetTexture(gbufferNormalHandle);
    Texture2D<float4> gbufferMaterial = GetTexture(gbufferMaterialHandle);
    Texture2D<float4> gbufferEmissive = GetTexture(gbufferEmissiveHandle);
    Texture2D<float4> gbufferDepth = GetTexture(gbufferDepthHandle);
    Texture2D<float4> ambientOcclusionTexture = GetTexture(ambientOcclusionHandle);
    SamplerState linearSampler = GetSampler(samplerHandle);

    float3 albedo = gbufferAlbedo.SampleLevel(linearSampler, uv, 0.0f).rgb;
    float3 normal = DecodeNormal(gbufferNormal.SampleLevel(linearSampler, uv, 0.0f).xyz);
    float4 material = gbufferMaterial.SampleLevel(linearSampler, uv, 0.0f);
    float3 emissive = gbufferEmissive.SampleLevel(linearSampler, uv, 0.0f).rgb;

    float occlusion = material.r;
    float roughness = material.g;
    float metallic = material.b;
    if (aoEnabled != 0u)
    {
        float aoFactor = ambientOcclusionTexture.SampleLevel(linearSampler, uv, 0.0f).r;
        occlusion = saturate(occlusion * aoFactor);
    }

    float depth = gbufferDepth.Load(int3(dispatchThreadId.xy, 0)).r;
    if (depth >= 0.9999f)
    {
        float3 skyDir = ReconstructWorldDirection(dispatchThreadId.xy, uint2(width, height), 1.0f, camera.invViewProjection);
        float3 skyColor = SampleEnvironment(environmentHandle, samplerHandle, skyDir);
        lightingTarget[dispatchThreadId.xy] = float4(skyColor, 1.0f);
        return;
    }

    float3 worldPos = ReconstructWorldPosition(dispatchThreadId.xy, uint2(width, height), depth, camera.invViewProjection);
    float3 viewPos = ReconstructViewPosition(dispatchThreadId.xy, uint2(width, height), depth, camera.invProjection);
    float3 viewDir = normalize(-worldPos);

    SurfaceLightingInput surface;
    surface.worldPos = worldPos;
    surface.normal = normal;
    surface.albedo = albedo;
    surface.metallic = metallic;
    surface.roughness = roughness;
    surface.occlusion = occlusion;
    surface.emissive = emissive;

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
        shadowTexelSizeBiasStrength.z,
        shadowTexelSizeBiasStrength.w,
        shadowsEnabled,
        shadowMaxDistance);

    uint clusterIndex = ComputeClusterIndex(dispatchThreadId.xy, uint2(framebufferSizeX, framebufferSizeY), max(viewPos.z, nearPlane), nearPlane, farPlane);
    ClusterHeader header = g_ClusterHeaders[clusterIndex];
    for (uint i = 0; i < header.count; ++i)
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

    lightingTarget[dispatchThreadId.xy] = float4(color, 1.0f);
}
