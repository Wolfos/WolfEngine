import "common.slang";
import "constants.slang";
import "common_bindless.slang";
import "common_lighting.slang";
import "ddgi_visibility.slang";

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
};

RWStructuredBuffer<PointLightData> g_PointLights : register(u3);
RWStructuredBuffer<ClusterHeader> g_ClusterHeaders : register(u4);
RWStructuredBuffer<uint> g_ClusterLightIndices : register(u5);

static const uint DDGI_VISIBILITY_TILE_INTERIOR_SIZE = 16u;
static const uint DDGI_TILE_BORDER_SIZE = 1u;
static const float DDGI_SH_BASIS_L0 = 0.28209479177f;
static const float DDGI_SH_BASIS_L1 = 0.48860251190f;
static const float DDGI_SH_DIRECTIONAL_LIMIT = 0.95f;

struct DdgiShSample
{
    float3 coefficientL0;
    float3 coefficientLy;
    float3 coefficientLz;
    float3 coefficientLx;
    uint valid;
};

int DdgiPositiveModulo(int value, int modulus)
{
    int remainder = value % max(modulus, 1);
    return remainder < 0 ? remainder + modulus : remainder;
}

uint DdgiPhysicalProbeIndex(uint logicalProbeIndex)
{
    uint x = logicalProbeIndex % ddgiProbeCountX;
    uint yz = logicalProbeIndex / ddgiProbeCountX;
    uint y = yz % ddgiProbeCountY;
    uint z = yz / ddgiProbeCountY;
    uint3 physical = uint3(
        DdgiPositiveModulo(int(x) + ddgiStorageOffsetX, int(ddgiProbeCountX)),
        DdgiPositiveModulo(int(y) + ddgiStorageOffsetY, int(ddgiProbeCountY)),
        DdgiPositiveModulo(int(z) + ddgiStorageOffsetZ, int(ddgiProbeCountZ)));
    return physical.x +
        physical.y * ddgiProbeCountX +
        physical.z * ddgiProbeCountX * ddgiProbeCountY;
}

bool DdgiProbeHasHistory(uint logicalProbeIndex)
{
    uint x = logicalProbeIndex % ddgiProbeCountX;
    uint yz = logicalProbeIndex / ddgiProbeCountX;
    uint y = yz % ddgiProbeCountY;
    uint z = yz / ddgiProbeCountY;
    int3 previous = int3(x, y, z) +
        int3(ddgiScrollDeltaX, ddgiScrollDeltaY, ddgiScrollDeltaZ);
    return previous.x >= 0 && previous.x < int(ddgiProbeCountX) &&
        previous.y >= 0 && previous.y < int(ddgiProbeCountY) &&
        previous.z >= 0 && previous.z < int(ddgiProbeCountZ);
}

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
    uint physicalProbeIndex = DdgiPhysicalProbeIndex(probeIndex);
    uint tileX = physicalProbeIndex % ddgiAtlasColumns;
    uint tileY = physicalProbeIndex / ddgiAtlasColumns;
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

float DdgiGridInfluence(float3 worldPos)
{
    if (ddgiEnabled == 0u || ddgiProbeCount == 0u)
    {
        return 0.0f;
    }

    float spacing = max(ddgiProbeSpacing, 0.001f);
    float3 probeMin = ddgiOrigin;
    float3 probeMax = ddgiOrigin + float3(
        max(ddgiProbeCountX, 1u) - 1u,
        max(ddgiProbeCountY, 1u) - 1u,
        max(ddgiProbeCountZ, 1u) - 1u) * spacing;
    float3 volumeMin = probeMin - 0.5f.xxx * spacing;
    float3 volumeMax = probeMax + 0.5f.xxx * spacing;
    float3 distanceInsideGrid = min(worldPos - volumeMin, volumeMax - worldPos);
    if (any(distanceInsideGrid < 0.0f.xxx))
    {
        return 0.0f;
    }

    float horizontalDistanceToBoundary = min(distanceInsideGrid.x, distanceInsideGrid.z);
    float horizontalInfluence = smoothstep(
        0.0f,
        max(ddgiHorizontalBlendDistance, 0.001f),
        horizontalDistanceToBoundary);
    float verticalDistanceToTopBoundary = volumeMax.y - worldPos.y;
    float verticalInfluence = smoothstep(
        0.0f,
        max(ddgiVerticalBlendDistance, 0.001f),
        verticalDistanceToTopBoundary);
    return min(horizontalInfluence, verticalInfluence);
}

DdgiShSample SampleDdgiSh(
    float3 worldPos,
    float3 normal,
    uint samplerHandle,
    float gridInfluence,
    uint probeDebugEnabled,
    out float debugBaseWeight,
    out float debugWeightedVisibility,
    out float3 debugDominantProbe,
    out float3 debugDominantProbeCoord,
    out float3 debugProbeRelocation,
    out float3 debugProbeRelocationDecision)
{
    DdgiShSample result = (DdgiShSample)0;
    debugBaseWeight = 0.0f;
    debugWeightedVisibility = 0.0f;
    debugDominantProbe = 0.0f.xxx;
    debugDominantProbeCoord = 0.0f.xxx;
    debugProbeRelocation = 0.0f.xxx;
    debugProbeRelocationDecision = 0.0f.xxx;
    if (gridInfluence <= 0.0f)
    {
        return result;
    }

    float3 biasedPos = worldPos + normal * ddgiViewBias;
    float3 grid = (biasedPos - ddgiOrigin) / max(ddgiProbeSpacing, 0.001f);
    float3 gridMax = float3(
        max(ddgiProbeCountX, 1u) - 1u,
        max(ddgiProbeCountY, 1u) - 1u,
        max(ddgiProbeCountZ, 1u) - 1u);
    float3 sampleGrid = clamp(grid, 0.0f.xxx, gridMax);
    float3 baseFloat = floor(sampleGrid);
    float3 fracPart = saturate(sampleGrid - baseFloat);
    int3 baseCoord = int3(baseFloat);
    Texture2D<float4> irradianceL0Tex = GetTexture(ddgiIrradianceL0Handle);
    Texture2D<float4> irradianceLyTex = GetTexture(ddgiIrradianceLyHandle);
    Texture2D<float4> irradianceLzTex = GetTexture(ddgiIrradianceLzHandle);
    Texture2D<float4> irradianceLxTex = GetTexture(ddgiIrradianceLxHandle);
    Texture2D<float4> visibilityTex = GetTexture(ddgiVisibilityHandle);
    Texture2D<float4> probeStateTex = GetTexture(ddgiProbeStateHandle);
    Texture2D<float4> probeActivityTex = GetTexture(ddgiProbeActivityHandle);
    Texture2D<float4> relocationDecisionTex = GetTexture(ddgiProbeRelocationDecisionHandle);
    SamplerState linearSampler = GetSampler(samplerHandle);

    float3 coefficientL0 = 0.0f.xxx;
    float3 coefficientLy = 0.0f.xxx;
    float3 coefficientLz = 0.0f.xxx;
    float3 coefficientLx = 0.0f.xxx;
    float baseWeightSum = 0.0f;
    float weightSum = 0.0f;
    float dominantProbeWeight = 0.0f;
    uint dominantProbeIndex = 0u;
    float3 dominantProbeCoord = 0.0f.xxx;
    float3 relocationDebugSum = 0.0f.xxx;
    float3 relocationDecisionDebugSum = 0.0f.xxx;
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
                float3 tri = lerp(1.0f.xxx - fracPart, fracPart, float3(x, y, z));
                float trilinearWeight = tri.x * tri.y * tri.z;
                uint physicalProbeIndex = DdgiPhysicalProbeIndex(probeIndex);
                if (probeActivityTex.Load(int3(uint2(
                    physicalProbeIndex % ddgiAtlasColumns,
                    physicalProbeIndex / ddgiAtlasColumns), 0)).x <= 0.5f)
                {
                    continue;
                }
                float4 probeState = probeStateTex.Load(int3(uint2(
                    physicalProbeIndex % ddgiAtlasColumns,
                    physicalProbeIndex / ddgiAtlasColumns), 0));
                if (uint(round(max(probeState.w, 0.0f))) != 1u)
                {
                    continue;
                }
                float3 relocationOffset =
                    ddgiProbeRelocationEnabled != 0u
                    ? probeState.xyz
                    : 0.0f.xxx;
                float3 probePosition = DdgiProbePosition(uint3(coord)) + relocationOffset;
                float3 toSurface = biasedPos - probePosition;
                float distanceToProbe = length(toSurface);
                float3 direction = distanceToProbe > 1e-4f ? toSurface / distanceToProbe : normal;
                float nWeight = saturate(dot(normal, -direction)) * 0.9f + 0.1f;
                float weight = trilinearWeight * nWeight;
                float2 visibilityUv = DdgiProbeAtlasUv(probeIndex, direction, DDGI_VISIBILITY_TILE_INTERIOR_SIZE);
                float2 moments = visibilityTex.SampleLevel(linearSampler, visibilityUv, 0.0f).rg;
                float normalizedDistance = saturate(distanceToProbe / max(ddgiMaxRayDistance, 0.001f));
                float meanDistance = moments.x;
                float meanDistanceSquared = moments.y;
                float visibility = DdgiEvaluateVisibility(meanDistance, meanDistanceSquared, normalizedDistance);
                float probeWeight = weight * visibility;
                baseWeightSum += weight;
                if (probeDebugEnabled != 0u && probeWeight > dominantProbeWeight)
                {
                    dominantProbeWeight = probeWeight;
                    dominantProbeIndex = physicalProbeIndex;
                    dominantProbeCoord = float3(coord.x, coord.y, coord.z);
                }
                if (probeDebugEnabled != 0u)
                {
                    relocationDebugSum += relocationOffset * probeWeight;
                    relocationDecisionDebugSum += relocationDecisionTex.Load(int3(uint2(
                        physicalProbeIndex % ddgiAtlasColumns,
                        physicalProbeIndex / ddgiAtlasColumns), 0)).rgb * probeWeight;
                }
                uint2 coefficientPixel = uint2(
                    physicalProbeIndex % ddgiAtlasColumns,
                    physicalProbeIndex / ddgiAtlasColumns);
                coefficientL0 += irradianceL0Tex.Load(int3(coefficientPixel, 0)).rgb * probeWeight;
                coefficientLy += irradianceLyTex.Load(int3(coefficientPixel, 0)).rgb * probeWeight;
                coefficientLz += irradianceLzTex.Load(int3(coefficientPixel, 0)).rgb * probeWeight;
                coefficientLx += irradianceLxTex.Load(int3(coefficientPixel, 0)).rgb * probeWeight;
                weightSum += probeWeight;
            }
        }
    }

    if (weightSum <= 1e-5f)
    {
        if (probeDebugEnabled != 0u)
        {
            debugBaseWeight = saturate(baseWeightSum);
        }
        return result;
    }

    float inverseWeight = rcp(weightSum);
    if (probeDebugEnabled != 0u)
    {
        debugBaseWeight = saturate(baseWeightSum);
        debugWeightedVisibility = saturate(weightSum / max(baseWeightSum, 1e-5f));
        uint probeHash = dominantProbeIndex * 747796405u + 2891336453u;
        probeHash = ((probeHash >> ((probeHash >> 28u) + 4u)) ^ probeHash) * 277803737u;
        probeHash = (probeHash >> 22u) ^ probeHash;
        float3 probeColor = float3(
            float(probeHash & 0xffu),
            float((probeHash >> 8u) & 0xffu),
            float((probeHash >> 16u) & 0xffu)) / 255.0f;
        probeColor = 0.25f.xxx + 0.75f * probeColor;
        debugDominantProbe = probeColor * saturate(dominantProbeWeight / weightSum);
        float3 probeCoordMax = max(float3(
            ddgiProbeCountX,
            ddgiProbeCountY,
            ddgiProbeCountZ) - 1.0f.xxx, 1.0f.xxx);
        debugDominantProbeCoord = saturate(dominantProbeCoord / probeCoordMax);

        float3 averageRelocationOffset = relocationDebugSum * inverseWeight;
        float signedVerticalRelocation =
            averageRelocationOffset.y / max(ddgiProbeSpacing, 0.001f) * 0.5f + 0.5f;
        debugProbeRelocation = float3(0.5f, signedVerticalRelocation, 0.5f);
        debugProbeRelocationDecision = relocationDecisionDebugSum * inverseWeight;
    }
    result.coefficientL0 = coefficientL0 * inverseWeight;
    result.coefficientLy = coefficientLy * inverseWeight;
    result.coefficientLz = coefficientLz * inverseWeight;
    result.coefficientLx = coefficientLx * inverseWeight;
    result.valid = 1u;
    return result;
}

float DdgiShLobeScale(float3 dc, float3 directionalX, float3 directionalY, float3 directionalZ)
{
    float3 directionalAmplitude = sqrt(
        directionalX * directionalX +
        directionalY * directionalY +
        directionalZ * directionalZ);
    float3 channelScale = select(
        directionalAmplitude > 1e-5f.xxx,
        dc * DDGI_SH_DIRECTIONAL_LIMIT / max(directionalAmplitude, 1e-5f.xxx),
        1.0f.xxx);
    return min(1.0f, min(channelScale.x, min(channelScale.y, channelScale.z)));
}

float3 EvaluateDdgiDiffuse(DdgiShSample sample, float3 normal)
{
    // L1 radiance SH convolved with a clamped cosine lobe and divided by PI.
    // Limit the directional lobe before evaluation so truncated L1 ringing cannot
    // drive individual color channels negative and create black or tinted bands.
    float3 dc = max(sample.coefficientL0 * DDGI_SH_BASIS_L0, 0.0f.xxx);
    float directionalScale = (2.0f / 3.0f) * DDGI_SH_BASIS_L1;
    float3 directionalX = sample.coefficientLx * directionalScale;
    float3 directionalY = sample.coefficientLy * directionalScale;
    float3 directionalZ = sample.coefficientLz * directionalScale;
    float lobeScale = DdgiShLobeScale(dc, directionalX, directionalY, directionalZ);
    float3 signedDirectional = lobeScale * (
        directionalY * normal.y +
        directionalZ * normal.z +
        directionalX * normal.x);
    float3 irradiance = dc + signedDirectional;
    return max(irradiance, 0.0f.xxx);
}

float3 EvaluateDdgiRadiance(DdgiShSample sample, float3 direction)
{
    direction = length(direction) > 1e-5f ? normalize(direction) : float3(0.0f, 0.0f, 1.0f);
    float3 dc = max(sample.coefficientL0 * DDGI_SH_BASIS_L0, 0.0f.xxx);
    float3 directionalX = sample.coefficientLx * DDGI_SH_BASIS_L1;
    float3 directionalY = sample.coefficientLy * DDGI_SH_BASIS_L1;
    float3 directionalZ = sample.coefficientLz * DDGI_SH_BASIS_L1;
    float lobeScale = DdgiShLobeScale(dc, directionalX, directionalY, directionalZ);
    float3 signedDirectional = lobeScale * (
        directionalY * direction.y +
        directionalZ * direction.z +
        directionalX * direction.x);
    return max(dc + signedDirectional, 0.0f.xxx);
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
    for (uint i = 0; i < header.count; ++i)
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
    float ddgiGridInfluence = DdgiGridInfluence(absoluteWorldPos);
    float3 F0 = lerp(float3(0.04f, 0.04f, 0.04f), albedo, metallic);
    float3 diffuseEnergy = (1.0f - metallic) * (1.0f - F0);
    float ddgiBaseWeightDebug;
    float ddgiWeightedVisibilityDebug;
    float3 ddgiDominantProbeDebug;
    float3 ddgiDominantProbeCoordDebug;
    float3 ddgiProbeRelocationDebug;
    float3 ddgiProbeRelocationDecisionDebug;
    DdgiShSample ddgiSample = SampleDdgiSh(
        absoluteWorldPos,
        normal,
        samplerHandle,
        ddgiGridInfluence,
        ddgiProbeDebugEnabled,
        ddgiBaseWeightDebug,
        ddgiWeightedVisibilityDebug,
        ddgiDominantProbeDebug,
        ddgiDominantProbeCoordDebug,
        ddgiProbeRelocationDebug,
        ddgiProbeRelocationDecisionDebug);
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
        ? max(ddgiGridInfluence * smoothstep(0.25f, 0.6f, roughness), 0.7f)
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
    color += lerp(skyDiffuseContribution, ddgiContribution, ddgiGridInfluence) + specularContribution;
    if (ddgiFinalContributionDebugEnabled != 0u)
    {
        RWTexture2D<float4> ddgiFinalContribution = GetRWTexture(ddgiFinalContributionHandle);
        ddgiFinalContribution[dispatchThreadId.xy] = float4(ddgiDiffuseIrradiance * ddgiGridInfluence, 1.0f);
    }
    if (ddgiProbeDebugEnabled != 0u)
    {
        GetRWTexture(ddgiProbeBaseWeightDebugHandle)[dispatchThreadId.xy] =
            float4(ddgiBaseWeightDebug.xxx, 1.0f);
        GetRWTexture(ddgiWeightedVisibilityDebugHandle)[dispatchThreadId.xy] =
            float4(ddgiWeightedVisibilityDebug.xxx, 1.0f);
        GetRWTexture(ddgiDominantProbeDebugHandle)[dispatchThreadId.xy] =
            float4(ddgiDominantProbeDebug, 1.0f);
        GetRWTexture(ddgiDominantProbeCoordDebugHandle)[dispatchThreadId.xy] =
            float4(ddgiDominantProbeCoordDebug, 1.0f);
        GetRWTexture(ddgiProbeRelocationDebugHandle)[dispatchThreadId.xy] =
            float4(saturate(ddgiProbeRelocationDebug), 1.0f);
        GetRWTexture(ddgiProbeRelocationDecisionDebugHandle)[dispatchThreadId.xy] =
            float4(saturate(ddgiProbeRelocationDecisionDebug), 1.0f);
    }
    color += surface.emissive;

    lightingTarget[dispatchThreadId.xy] = float4(color, 1.0f);
}
