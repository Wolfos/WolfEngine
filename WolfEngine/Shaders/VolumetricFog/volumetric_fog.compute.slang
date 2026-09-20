import "../Common/common_bindless.slang";
import "../Common/common_lighting.slang";
import "../Common/constants.slang";
import "../Common/volumetric_fog.slang";

struct FogVolumeData { row_major float4x4 worldToLocal; float4 albedoExtinction; float4 parameters; };
struct FogCellHeader { uint offset; uint count; };
StructuredBuffer<FogVolumeData> g_FogVolumes : register(t10);
StructuredBuffer<FogCellHeader> g_FogCellHeaders : register(t11);
StructuredBuffer<uint> g_FogVolumeIndices : register(t12);

cbuffer BindlessHandles : register(b0)
{
    uint currentHandle; uint historyHandle; uint outputHandle; uint irradianceHandle;
    uint linearSamplerHandle; uint shadowMapHandle0; uint shadowMapHandle1; uint shadowMapHandle2;
    uint shadowSamplerHandle; uint _bindlessPadding0; uint _bindlessPadding1; uint _bindlessPadding2;
};

cbuffer FogParams : register(b1)
{
    row_major float4x4 inverseUnjitteredViewProjection;
    row_major float4x4 viewMatrix;
    row_major float4x4 previousViewProjection;
    float3 cameraOrigin; float extinction;
    float3 previousCameraOrigin; float baseHeight;
    float3 albedo; float heightFalloff;
    uint gridSizeX; uint gridSizeY; uint gridSizeZ; uint frameIndex;
    uint cellGridSizeX; uint cellGridSizeY; uint cellGridSizeZ; uint historyValid;
    uint volumeCount; float anisotropy; float nearPlane; float maxDistance;
    float historyWeight; float _fogPadding0; float _fogPadding1; float _fogPadding2;
};

cbuffer FogLightingParams : register(b2)
{
    float4 lightColorIntensity;
    float4 lightDirectionEnabled;
    row_major float4x4 shadowViewProjection0;
    row_major float4x4 shadowViewProjection1;
    row_major float4x4 shadowViewProjection2;
    float4 shadowSplitsBlend;
    float4 shadowTexelSizeEnabled;
    float4 shadowDepthBiases;
    float shadowMaxDistance; float _lightingPadding0; float _lightingPadding1; float _lightingPadding2;
};

float Hash13(uint3 p)
{
    uint n = p.x * 1597334677u ^ p.y * 3812015801u ^ p.z * 2798796415u ^ frameIndex * 277803737u;
    n ^= n >> 16; n *= 2246822519u; n ^= n >> 13;
    return float(n & 0x00ffffffu) / 16777216.0f;
}

float SliceSampleDepth(uint slice, float jitter)
{
    return lerp(FogSliceBoundary(slice, gridSizeZ, nearPlane, maxDistance),
        FogSliceBoundary(slice + 1u, gridSizeZ, nearPlane, maxDistance), jitter);
}

float3 ReconstructRelativePosition(uint3 froxel, float viewDepth)
{
    // Froxel XY follows screen UV (origin top-left), so NDC Y is flipped.
    float2 uv = (float2(froxel.xy) + 0.5f.xx) / float2(gridSizeX, gridSizeY);
    float2 ndc = float2(uv.x * 2.0f - 1.0f, 1.0f - uv.y * 2.0f);
    float4 farH = mul(float4(ndc, 1.0f, 1.0f), inverseUnjitteredViewProjection);
    float3 farRelative = farH.xyz / farH.w;
    float3 farView = mul(float4(farRelative, 1.0f), viewMatrix).xyz;
    return farRelative * (viewDepth / max(farView.z, 1e-5f));
}

float PhaseHenyeyGreenstein(float cosine, float g)
{
    float g2 = g * g;
    return (1.0f - g2) / max(4.0f * PI * pow(1.0f + g2 - 2.0f * g * cosine, 1.5f), 1e-5f);
}

float VolumeWeight(FogVolumeData volume, float3 worldPosition)
{
    float3 local = mul(float4(worldPosition, 1.0f), volume.worldToLocal).xyz;
    float distanceInside = volume.parameters.z < 0.5f
        ? 0.5f - max(abs(local.x), max(abs(local.y), abs(local.z)))
        : (1.0f - length(local * 2.0f)) * 0.5f;
    if (distanceInside <= 0.0f) return 0.0f;
    return volume.parameters.y <= 1e-5f ? 1.0f : smoothstep(0.0f, volume.parameters.y, distanceInside);
}

float ShadowVisibility(float3 relativePosition, float viewDepth, float3 lightDirection)
{
    if (shadowTexelSizeEnabled.z < 0.5f || viewDepth > shadowMaxDistance) return 1.0f;
    return ComputeCascadedDirectionalShadow(relativePosition, lightDirection, lightDirection, viewDepth,
        shadowViewProjection0, shadowViewProjection1, shadowViewProjection2,
        shadowMapHandle0, shadowMapHandle1, shadowMapHandle2, shadowSamplerHandle,
        shadowSplitsBlend.xyz, shadowSplitsBlend.w, shadowTexelSizeEnabled.xy, shadowDepthBiases.xyz);
}

void AddMedium(float mediumExtinction, float3 mediumAlbedo, float mediumAnisotropy,
    float3 ambientRadiance, float3 sunRadiance, float sunVisibility, float phaseCosine,
    inout float3 source, inout float totalExtinction)
{
    if (mediumExtinction <= 0.0f) return;
    float3 sigmaS = mediumExtinction * saturate(mediumAlbedo);
    source += sigmaS * (ambientRadiance + sunRadiance * sunVisibility *
        PhaseHenyeyGreenstein(phaseCosine, mediumAnisotropy));
    totalExtinction += mediumExtinction;
}

[shader("compute")]
[numthreads(4, 4, 4)]
void VolumetricFogInjectCS(uint3 id : SV_DispatchThreadID)
{
    if (any(id >= uint3(gridSizeX, gridSizeY, gridSizeZ))) return;
    float depth = SliceSampleDepth(id.z, Hash13(id));
    float3 relativePosition = ReconstructRelativePosition(id, depth);
    float3 worldPosition = relativePosition + cameraOrigin;
    float3 lightDirection = normalize(-lightDirectionEnabled.xyz);
    float phaseCosine = dot(normalize(relativePosition), lightDirection);
    float3 ambient = SampleEnvironment(irradianceHandle, linearSamplerHandle, float3(0.0f, 1.0f, 0.0f)) / PI;
    float3 sun = lightColorIntensity.rgb * lightColorIntensity.a * lightDirectionEnabled.w;
    float visibility = ShadowVisibility(relativePosition, depth, lightDirection);
    float3 source = 0.0f.xxx;
    float totalExtinction = 0.0f;
    float heightDensity = extinction * exp(-max(worldPosition.y - baseHeight, 0.0f) * max(heightFalloff, 0.0f));
    AddMedium(heightDensity, albedo, anisotropy, ambient, sun, visibility, phaseCosine, source, totalExtinction);

    uint3 cell = id / 4u;
    uint cellIndex = cell.x + cellGridSizeX * (cell.y + cellGridSizeY * cell.z);
    FogCellHeader header = g_FogCellHeaders[cellIndex];
    for (uint candidate = 0u; candidate < header.count; candidate++)
    {
        uint volumeIndex = g_FogVolumeIndices[header.offset + candidate];
        if (volumeIndex >= volumeCount) continue;
        FogVolumeData volume = g_FogVolumes[volumeIndex];
        float weight = VolumeWeight(volume, worldPosition);
        AddMedium(volume.albedoExtinction.a * weight, volume.albedoExtinction.rgb, volume.parameters.x,
            ambient, sun, visibility, phaseCosine, source, totalExtinction);
    }
    GetRWTexture3D(outputHandle)[id] = float4(source, totalExtinction);
}

[shader("compute")]
[numthreads(4, 4, 4)]
void VolumetricFogTemporalCS(uint3 id : SV_DispatchThreadID)
{
    if (any(id >= uint3(gridSizeX, gridSizeY, gridSizeZ))) return;
    Texture3D<float4> current = GetTexture3D(currentHandle);
    float4 currentValue = current.Load(int4(id, 0));
    if (historyValid == 0u) { GetRWTexture3D(outputHandle)[id] = currentValue; return; }
    float depth = SliceSampleDepth(id.z, 0.5f);
    float3 worldPosition = ReconstructRelativePosition(id, depth) + cameraOrigin;
    float4 previousClip = mul(float4(worldPosition - previousCameraOrigin, 1.0f), previousViewProjection);
    if (previousClip.w <= 1e-5f) { GetRWTexture3D(outputHandle)[id] = currentValue; return; }
    float2 previousNdc = previousClip.xy / previousClip.w;
    float2 previousUv = float2(previousNdc.x * 0.5f + 0.5f, 0.5f - previousNdc.y * 0.5f);
    float previousW = FogDepthToVolumeW(previousClip.w, gridSizeZ, nearPlane, maxDistance);
    if (any(previousUv < 0.0f.xx) || any(previousUv > 1.0f.xx) || previousW < 0.0f || previousW > 1.0f)
    { GetRWTexture3D(outputHandle)[id] = currentValue; return; }
    float4 neighborhoodMin = currentValue;
    float4 neighborhoodMax = currentValue;
    [unroll] for (int z = -1; z <= 1; z++)
    [unroll] for (int y = -1; y <= 1; y++)
    [unroll] for (int x = -1; x <= 1; x++)
    {
        int3 c = clamp(int3(id) + int3(x, y, z), int3(0), int3(gridSizeX, gridSizeY, gridSizeZ) - 1);
        float4 v = current.Load(int4(c, 0));
        neighborhoodMin = min(neighborhoodMin, v); neighborhoodMax = max(neighborhoodMax, v);
    }
    float4 history = GetTexture3D(historyHandle).SampleLevel(
        GetSampler(linearSamplerHandle), float3(previousUv, previousW), 0.0f);
    history = clamp(history, neighborhoodMin, neighborhoodMax);
    GetRWTexture3D(outputHandle)[id] = lerp(currentValue, history, saturate(historyWeight));
}

[shader("compute")]
[numthreads(8, 8, 1)]
void VolumetricFogIntegrateCS(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= gridSizeX || id.y >= gridSizeY) return;
    Texture3D<float4> sourceVolume = GetTexture3D(currentHandle);
    RWTexture3D<float4> integrated = GetRWTexture3D(outputHandle);
    float3 scattering = 0.0f.xxx; float transmittance = 1.0f;
    for (uint z = 0u; z < gridSizeZ; z++)
    {
        float4 medium = sourceVolume.Load(int4(id.xy, z, 0));
        float segmentLength = FogSliceBoundary(z + 1u, gridSizeZ, nearPlane, maxDistance) -
            FogSliceBoundary(z, gridSizeZ, nearPlane, maxDistance);
        float segmentTransmittance = exp(-max(medium.a, 0.0f) * segmentLength);
        float integral = medium.a > 1e-5f ? (1.0f - segmentTransmittance) / medium.a : segmentLength;
        scattering += transmittance * medium.rgb * integral;
        transmittance *= segmentTransmittance;
        integrated[uint3(id.xy, z)] = float4(scattering, transmittance);
    }
}
