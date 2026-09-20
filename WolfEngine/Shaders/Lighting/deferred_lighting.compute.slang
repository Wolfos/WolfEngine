import "../Common/common.slang";
import "../Common/constants.slang";
import "../Common/common_bindless.slang";
import "../Common/common_lighting.slang";
import "../Ddgi/ddgi_sample.slang";
import "../Common/volumetric_fog.slang";

cbuffer BindlessHandles : register(b0)
{
    uint gbufferAlbedoHandle;
    uint gbufferNormalHandle;
    uint gbufferMaterialHandle;
    uint gbufferEmissiveHandle;
    uint gbufferDepthHandle;
    uint ambientOcclusionHandle;
    uint reflectionsHandle;
    uint ddgiIrradianceL0Handle;
    uint ddgiIrradianceLyHandle;
    uint ddgiIrradianceLzHandle;
    uint ddgiIrradianceLxHandle;
    uint ddgiVisibilityHandle;
    uint ddgiProbeStateHandle;
    uint ddgiProbeActivityHandle;
    uint ddgiProbeRelocationDecisionHandle;
    uint ddgiFinalContributionHandle;
    uint ddgiProbeBaseWeightDebugHandle;
    uint ddgiWeightedVisibilityDebugHandle;
    uint ddgiDominantProbeDebugHandle;
    uint ddgiDominantProbeCoordDebugHandle;
    uint ddgiProbeRelocationDebugHandle;
    uint ddgiProbeRelocationDecisionDebugHandle;
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
    uint fogIntegratedHandle;
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
    uint reflectionsEnabled;
    uint ddgiEnabled;
    uint ddgiFinalContributionDebugEnabled;
    uint ddgiProbeDebugEnabled;
    DirectionalLightData directionalLights[MAX_DIRECTIONAL_LIGHTS];
    row_major float4x4 shadowViewProjection0;
    row_major float4x4 shadowViewProjection1;
    row_major float4x4 shadowViewProjection2;
    float4 shadowSplitsBlend;
    float4 shadowTexelSizeBiasStrength;
    float4 shadowDepthBiases;
    float3 ddgiOrigin;
    float ddgiProbeSpacing;
    int ddgiStorageOffsetX;
    int ddgiStorageOffsetY;
    int ddgiStorageOffsetZ;
    int ddgiScrollDeltaX;
    int ddgiScrollDeltaY;
    int ddgiScrollDeltaZ;
    uint ddgiScrollingPad0;
    uint ddgiScrollingPad1;
    uint ddgiProbeCountX;
    uint ddgiProbeCountY;
    uint ddgiProbeCountZ;
    uint ddgiProbeCount;
    uint ddgiAtlasColumns;
    uint ddgiAtlasRows;
    float ddgiMaxRayDistance;
    float ddgiViewBias;
    float ddgiHorizontalBlendDistance;
    float ddgiVerticalBlendDistance;
    uint ddgiProbeRelocationEnabled;
    float shadowMaxDistance;
    float nearPlane;
    float farPlane;
    uint framebufferSizeX;
    uint framebufferSizeY;
    float diffuseIndirectMultiplier;
    float specularIndirectMultiplier;
    uint fogEnabled;
    uint fogSliceCount;
    float fogMaxDistance;
    float _fogPadding;
};

RWStructuredBuffer<PointLightData> g_PointLights : register(u3);
RWStructuredBuffer<ClusterHeader> g_ClusterHeaders : register(u4);
RWStructuredBuffer<uint> g_ClusterLightIndices : register(u5);

/// Packs the DDGI fields of LightingParams into the shared sampler's parameter
/// struct. The probe update passes bind the same values as DdgiSettings at b1,
/// so ddgi_sample.slang takes them as an argument instead of reading a cbuffer
/// and can be shared by both.
DdgiSampleParams MakeDdgiSampleParams()
{
    DdgiSampleParams params;
    params.origin = ddgiOrigin;
    params.probeSpacing = ddgiProbeSpacing;
    params.probeCount = uint3(ddgiProbeCountX, ddgiProbeCountY, ddgiProbeCountZ);
    params.totalProbeCount = ddgiProbeCount;
    params.storageOffset = int3(ddgiStorageOffsetX, ddgiStorageOffsetY, ddgiStorageOffsetZ);
    params.atlasColumns = ddgiAtlasColumns;
    params.atlasRows = ddgiAtlasRows;
    params.tileBorderSize = DDGI_TILE_BORDER_SIZE;
    params.maxRayDistance = ddgiMaxRayDistance;
    params.viewBias = ddgiViewBias;
    params.horizontalBlendDistance = ddgiHorizontalBlendDistance;
    params.verticalBlendDistance = ddgiVerticalBlendDistance;
    params.enabled = ddgiEnabled;
    params.probeRelocationEnabled = ddgiProbeRelocationEnabled;
    params.irradianceL0Handle = ddgiIrradianceL0Handle;
    params.irradianceLyHandle = ddgiIrradianceLyHandle;
    params.irradianceLzHandle = ddgiIrradianceLzHandle;
    params.irradianceLxHandle = ddgiIrradianceLxHandle;
    params.visibilityHandle = ddgiVisibilityHandle;
    params.probeStateHandle = ddgiProbeStateHandle;
    params.probeActivityHandle = ddgiProbeActivityHandle;
    params.relocationDecisionHandle = ddgiProbeRelocationDecisionHandle;
    params.samplerHandle = samplerHandle;
    return params;
}


float3 ReconstructCameraRelativeWorldPosition(uint2 pixel, uint2 size, float depth, float4x4 invViewProjection)
{
    float2 ndc;
    ndc.x = (float(pixel.x) + 0.5f) / float(size.x) * 2.0f - 1.0f;
    ndc.y = -((float(pixel.y) + 0.5f) / float(size.y) * 2.0f - 1.0f);

    float4 clip = float4(ndc, depth, 1.0f);
    float4 cameraRelativeWorldPosH = mul(clip, invViewProjection);
    return cameraRelativeWorldPosH.xyz / cameraRelativeWorldPosH.w;
}

[numthreads(8, 8, 1)]
void DeferredLightingCS(uint3 dispatchThreadId : SV_DispatchThreadID)
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
    if (IsBackgroundDepth(depth))
    {
        float3 skyDir = ReconstructWorldDirection(dispatchThreadId.xy, uint2(width, height), 1.0f, camera.invViewProjection);
        float3 skyColor = SampleEnvironment(environmentHandle, samplerHandle, skyDir);
        if (fogEnabled != 0u)
        {
            float4 fog = SampleIntegratedFog(fogIntegratedHandle, samplerHandle, uv,
                fogMaxDistance, fogSliceCount, nearPlane, fogMaxDistance);
            skyColor = skyColor * fog.a + fog.rgb;
        }
        if (ddgiFinalContributionDebugEnabled != 0u)
        {
            GetRWTexture(ddgiFinalContributionHandle)[dispatchThreadId.xy] = float4(0.0f, 0.0f, 0.0f, 1.0f);
        }
        if (ddgiProbeDebugEnabled != 0u)
        {
            GetRWTexture(ddgiProbeBaseWeightDebugHandle)[dispatchThreadId.xy] = float4(0.0f, 0.0f, 0.0f, 1.0f);
            GetRWTexture(ddgiWeightedVisibilityDebugHandle)[dispatchThreadId.xy] = float4(0.0f, 0.0f, 0.0f, 1.0f);
            GetRWTexture(ddgiDominantProbeDebugHandle)[dispatchThreadId.xy] = float4(0.0f, 0.0f, 0.0f, 1.0f);
            GetRWTexture(ddgiDominantProbeCoordDebugHandle)[dispatchThreadId.xy] = float4(0.0f, 0.0f, 0.0f, 1.0f);
            GetRWTexture(ddgiProbeRelocationDebugHandle)[dispatchThreadId.xy] = float4(0.0f, 0.0f, 0.0f, 1.0f);
            GetRWTexture(ddgiProbeRelocationDecisionDebugHandle)[dispatchThreadId.xy] = float4(0.0f, 0.0f, 0.0f, 1.0f);
        }
        lightingTarget[dispatchThreadId.xy] = float4(skyColor, 1.0f);
        return;
    }

    float3 cameraRelativeWorldPos = ReconstructCameraRelativeWorldPosition(dispatchThreadId.xy, uint2(width, height), depth, camera.invViewProjection);
    float3 absoluteWorldPos = cameraRelativeWorldPos + camera.cameraOrigin;
    float3 viewPos = ReconstructViewPosition(dispatchThreadId.xy, uint2(width, height), depth, camera.invProjection);
    float3 viewDir = normalize(-cameraRelativeWorldPos);

    SurfaceLightingInput surface;
    surface.worldPos = cameraRelativeWorldPos;
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
        shadowDepthBiases.xyz,
        shadowTexelSizeBiasStrength.w,
        shadowsEnabled,
        shadowMaxDistance);

    uint clusterIndex = ComputeClusterIndex(dispatchThreadId.xy, uint2(framebufferSizeX, framebufferSizeY), max(viewPos.z, nearPlane), nearPlane, farPlane);
    ClusterHeader header = g_ClusterHeaders[clusterIndex];

    uint pointLightIterations = min(header.count, CLUSTER_MAX_LIGHTS_PER_CLUSTER);
    for (uint i = 0; i < pointLightIterations; ++i)
    {
        uint pointLightIndex = g_ClusterLightIndices[header.offset + i];
        color += EvaluatePointLight(surface, viewDir, g_PointLights[pointLightIndex]);
    }

    float3 skyDiffuseContribution = EvaluateDiffuseImageBasedLighting(
        surface,
        irradianceHandle,
        samplerHandle,
        IBL_EXPOSURE);
    float3 skySpecularContribution = EvaluateSpecularImageBasedLighting(
        surface,
        viewDir,
        prefilteredHandle,
        brdfLutHandle,
        samplerHandle,
        IBL_EXPOSURE);
    DdgiSampleParams ddgiParams = MakeDdgiSampleParams();
    float ddgiGridInfluence = DdgiGridInfluence(ddgiParams, absoluteWorldPos);
    float3 F0 = lerp(float3(0.04f, 0.04f, 0.04f), albedo, metallic);
    float3 diffuseEnergy = (1.0f - metallic) * (1.0f - F0);
    DdgiProbeDebug ddgiDebug;
    DdgiShSample ddgiSample = SampleDdgiSh(
        ddgiParams,
        absoluteWorldPos,
        normal,
        ddgiGridInfluence,
        ddgiProbeDebugEnabled,
        ddgiDebug);
    float3 ddgiDiffuseIrradiance = EvaluateDdgiDiffuse(ddgiSample, normal);
    float3 ddgiContribution =
        ddgiDiffuseIrradiance * diffuseEnergy * albedo * occlusion;
    float3 reflectionDirection = reflect(-viewDir, normal);
    float3 ddgiSpecularRadiance = EvaluateDdgiRadiance(ddgiSample, reflectionDirection);
    float3 ddgiSpecularContribution = EvaluateSpecularImageBasedLightingFromRadiance(
        surface,
        viewDir,
        ddgiSpecularRadiance,
        brdfLutHandle,
        samplerHandle);
    float ddgiSpecularBlend = ddgiSample.valid != 0u
        ? max(ddgiGridInfluence * smoothstep(0.25f, 0.6f, roughness), 1.0f)
        : 0.0f;
    float3 specularContribution = lerp(
        skySpecularContribution,
        ddgiSpecularContribution,
        ddgiSpecularBlend);
    if (reflectionsEnabled != 0u)
    {
        // The reflection buffer carries traced incident radiance plus the weight with which it
        // should replace the probe/environment estimate for this pixel's specular lobe.
        float4 reflectionSample = GetTexture(reflectionsHandle).Load(int3(dispatchThreadId.xy, 0));
        float3 reflectionSpecularContribution = EvaluateSpecularImageBasedLightingFromRadiance(
            surface,
            viewDir,
            reflectionSample.rgb,
            brdfLutHandle,
            samplerHandle);
        specularContribution = lerp(
            specularContribution,
            reflectionSpecularContribution,
            saturate(reflectionSample.a));
    }
    float3 diffuseIndirectContribution = lerp(
        skyDiffuseContribution,
        ddgiContribution,
        ddgiGridInfluence);
    color += diffuseIndirectContribution * diffuseIndirectMultiplier +
        specularContribution * specularIndirectMultiplier;
    if (ddgiFinalContributionDebugEnabled != 0u)
    {
        RWTexture2D<float4> ddgiFinalContribution = GetRWTexture(ddgiFinalContributionHandle);
        ddgiFinalContribution[dispatchThreadId.xy] = float4(ddgiDiffuseIrradiance * ddgiGridInfluence, 1.0f);
    }
    if (ddgiProbeDebugEnabled != 0u)
    {
        GetRWTexture(ddgiProbeBaseWeightDebugHandle)[dispatchThreadId.xy] =
            float4(ddgiDebug.baseWeight.xxx, 1.0f);
        GetRWTexture(ddgiWeightedVisibilityDebugHandle)[dispatchThreadId.xy] =
            float4(ddgiDebug.weightedVisibility.xxx, 1.0f);
        GetRWTexture(ddgiDominantProbeDebugHandle)[dispatchThreadId.xy] =
            float4(ddgiDebug.dominantProbe, 1.0f);
        GetRWTexture(ddgiDominantProbeCoordDebugHandle)[dispatchThreadId.xy] =
            float4(ddgiDebug.dominantProbeCoord, 1.0f);
        GetRWTexture(ddgiProbeRelocationDebugHandle)[dispatchThreadId.xy] =
            float4(saturate(ddgiDebug.probeRelocation), 1.0f);
        GetRWTexture(ddgiProbeRelocationDecisionDebugHandle)[dispatchThreadId.xy] =
            float4(saturate(ddgiDebug.probeRelocationDecision), 1.0f);
    }
    color += surface.emissive;

    if (fogEnabled != 0u)
    {
        float4 fog = SampleIntegratedFog(fogIntegratedHandle, samplerHandle, uv,
            viewPos.z, fogSliceCount, nearPlane, fogMaxDistance);
        color = color * fog.a + fog.rgb;
    }

    lightingTarget[dispatchThreadId.xy] = float4(color, 1.0f);
}
