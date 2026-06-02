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
    uint ddgiIrradianceHandle;
    uint ddgiVisibilityHandle;
    uint ddgiFinalContributionHandle;
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
    uint ddgiEnabled;
    DirectionalLightData directionalLights[MAX_DIRECTIONAL_LIGHTS];
    row_major float4x4 shadowViewProjection0;
    row_major float4x4 shadowViewProjection1;
    row_major float4x4 shadowViewProjection2;
    float4 shadowSplitsBlend;
    float4 shadowTexelSizeBiasStrength;
    float4 shadowDepthBiases;
    float3 ddgiOrigin;
    float ddgiProbeSpacing;
    uint ddgiProbeCountX;
    uint ddgiProbeCountY;
    uint ddgiProbeCountZ;
    uint ddgiProbeCount;
    uint ddgiAtlasColumns;
    uint ddgiAtlasRows;
    float ddgiMaxRayDistance;
    float ddgiViewBias;
    float shadowMaxDistance;
    float nearPlane;
    float farPlane;
    uint framebufferSizeX;
    uint framebufferSizeY;
};

RWStructuredBuffer<PointLightData> g_PointLights : register(u3);
RWStructuredBuffer<ClusterHeader> g_ClusterHeaders : register(u4);
RWStructuredBuffer<uint> g_ClusterLightIndices : register(u5);

static const uint DDGI_IRRADIANCE_TILE_INTERIOR_SIZE = 8u;
static const uint DDGI_VISIBILITY_TILE_INTERIOR_SIZE = 16u;
static const uint DDGI_TILE_BORDER_SIZE = 1u;

float2 DdgiOctEncode(float3 n)
{
    n /= (abs(n.x) + abs(n.y) + abs(n.z));
    float2 enc = n.xy;
    if (n.z < 0.0f)
    {
        enc = (1.0f - abs(enc.yx)) * select(enc.xy >= 0.0f, 1.0f.xx, -1.0f.xx);
    }
    return enc;
}

float2 DdgiProbeAtlasUv(uint probeIndex, float3 direction, uint tileInteriorSize)
{
    uint tileSize = tileInteriorSize + DDGI_TILE_BORDER_SIZE * 2u;
    uint tileX = probeIndex % ddgiAtlasColumns;
    uint tileY = probeIndex / ddgiAtlasColumns;
    float2 oct = DdgiOctEncode(normalize(direction));
    float2 interiorUv = oct * 0.5f + 0.5f;
    float2 atlasPixel = float2(tileX * tileSize + DDGI_TILE_BORDER_SIZE, tileY * tileSize + DDGI_TILE_BORDER_SIZE) +
        interiorUv * float(tileInteriorSize - 1u) + 0.5f.xx;
    return atlasPixel / float2(ddgiAtlasColumns * tileSize, ddgiAtlasRows * tileSize);
}

float3 DdgiProbePosition(uint3 probeCoord)
{
    return ddgiOrigin + float3(probeCoord) * ddgiProbeSpacing;
}

float3 SampleDdgi(float3 worldPos, float3 normal, float3 viewDir, float metallic, uint samplerHandle)
{
    if (ddgiEnabled == 0u || ddgiProbeCount == 0u)
    {
        return 0.0f.xxx;
    }

    float3 biasedPos = worldPos + normal * ddgiViewBias;
    float3 grid = (biasedPos - ddgiOrigin) / max(ddgiProbeSpacing, 0.001f);
    float3 baseFloat = floor(grid);
    float3 fracPart = saturate(grid - baseFloat);
    int3 baseCoord = int3(baseFloat);
    Texture2D<float4> irradianceTex = GetTexture(ddgiIrradianceHandle);
    Texture2D<float4> visibilityTex = GetTexture(ddgiVisibilityHandle);
    SamplerState linearSampler = GetSampler(samplerHandle);

    float3 result = 0.0f.xxx;
    float weightSum = 0.0f;
    for (uint z = 0u; z <= 1u; z++)
    {
        for (uint y = 0u; y <= 1u; y++)
        {
            for (uint x = 0u; x <= 1u; x++)
            {
                int3 coord = baseCoord + int3(x, y, z);
                if (coord.x < 0 || coord.y < 0 || coord.z < 0 ||
                    coord.x >= int(ddgiProbeCountX) || coord.y >= int(ddgiProbeCountY) || coord.z >= int(ddgiProbeCountZ))
                {
                    continue;
                }

                uint probeIndex = uint(coord.x) + uint(coord.y) * ddgiProbeCountX + uint(coord.z) * ddgiProbeCountX * ddgiProbeCountY;
                float3 probePosition = DdgiProbePosition(uint3(coord));
                float3 toSurface = biasedPos - probePosition;
                float distanceToProbe = length(toSurface);
                float3 direction = distanceToProbe > 1e-4f ? toSurface / distanceToProbe : normal;
                float nWeight = saturate(dot(normal, -direction)) * 0.9f + 0.1f;
                float3 tri = lerp(1.0f.xxx - fracPart, fracPart, float3(x, y, z));
                float weight = tri.x * tri.y * tri.z * nWeight;
                float2 visibilityUv = DdgiProbeAtlasUv(probeIndex, direction, DDGI_VISIBILITY_TILE_INTERIOR_SIZE);
                float2 moments = visibilityTex.SampleLevel(linearSampler, visibilityUv, 0.0f).rg;
                float meanDistance = moments.x * ddgiMaxRayDistance;
                float visibility = distanceToProbe <= meanDistance + ddgiProbeSpacing ? 1.0f : 0.15f;
                float2 irradianceUv = DdgiProbeAtlasUv(probeIndex, normal, DDGI_IRRADIANCE_TILE_INTERIOR_SIZE);
                float3 irradiance = irradianceTex.SampleLevel(linearSampler, irradianceUv, 0.0f).rgb;
                result += irradiance * weight * visibility;
                weightSum += weight;
            }
        }
    }

    if (weightSum <= 1e-5f)
    {
        return 0.0f.xxx;
    }

    return result / weightSum * (1.0f - metallic);
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
    if (IsBackgroundDepth(depth))
    {
        float3 skyDir = ReconstructWorldDirection(dispatchThreadId.xy, uint2(width, height), 1.0f, camera.invViewProjection);
        float3 skyColor = SampleEnvironment(environmentHandle, samplerHandle, skyDir);
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
    float3 ddgiContribution = SampleDdgi(absoluteWorldPos, normal, viewDir, metallic, samplerHandle) * albedo;
    color += ddgiContribution;
    if (ddgiEnabled != 0u)
    {
        RWTexture2D<float4> ddgiFinalContribution = GetRWTexture(ddgiFinalContributionHandle);
        ddgiFinalContribution[dispatchThreadId.xy] = float4(ddgiContribution, 1.0f);
    }
    color += surface.emissive;

    lightingTarget[dispatchThreadId.xy] = float4(color, 1.0f);
}
