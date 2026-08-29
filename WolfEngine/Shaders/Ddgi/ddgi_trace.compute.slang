import "../Common/common_bindless.slang";
import "../Common/draw_data.slang";
import "../Common/packed_vertex.slang";
import "../Common/raytracing_common.slang";
import "ddgi_common.slang";
import "../Terrain/terrain_layer_blend.slang";

#define WOLF_MAX_INSTANCE_COUNT 65535
#define WOLF_MAX_MATERIAL_COUNT 65535
#define WOLF_DRAW_KIND_TERRAIN 2u

RaytracingAccelerationStructure sceneTlas : register(t3);
StructuredBuffer<GpuInstanceData> g_InstanceTable : register(t4);
StructuredBuffer<GpuMaterialData> g_MaterialTable : register(t5);
StructuredBuffer<uint> g_InstanceIndexToInstanceHandle : register(t6);
StructuredBuffer<GpuMeshData> g_MeshTable : register(t7);
StructuredBuffer<PackedMeshVertex> g_PackedVertices : register(t8);
StructuredBuffer<uint> g_PackedIndices : register(t9);
StructuredBuffer<GpuTerrainMaterialData> g_TerrainMaterialTable : register(t10);
StructuredBuffer<GpuTerrainLayerData> g_TerrainLayerTable : register(t11);
StructuredBuffer<uint> g_InstanceIndexToTerrainRtResolution : register(t12);

struct DdgiTraceResult
{
    float3 radiance;
    float hitDistance;
    bool hitGeometry;
    bool backfaceHit;
};

struct DdgiHitSurface
{
    bool valid;
    float3 worldPosition;
    float3 worldNormal;
    float3 worldGeometricNormal;
    float2 uv;
    float3 albedo;
    float3 emissive;
};

struct DdgiTerrainLayerSample
{
    float4 albedo;
    float height;
};

uint DdgiResolveMaterialIndex(uint materialHandle)
{
    if (!IsPackedHandleValid(materialHandle))
    {
        return 0u;
    }

    uint materialIndex = GetHandleIndex(materialHandle);
    return materialIndex > 0u && materialIndex < WOLF_MAX_MATERIAL_COUNT ? materialIndex : 0u;
}

float DdgiTerrainCubicHermite(float a, float b, float c, float d, float t)
{
    float t2 = t * t;
    float t3 = t2 * t;
    return b + 0.5f * t * (c - a + t * (2.0f * a - 5.0f * b + 4.0f * c - d + t * (3.0f * (b - c) + d - a)));
}

float DdgiSampleTerrainHeight(float2 uv, GpuTerrainMaterialData terrainData)
{
    Texture2D<float4> heightmap = GetTexture(terrainData.heightmapHandle);
    uint width = 1;
    uint height = 1;
    heightmap.GetDimensions(width, height);
    if (width <= 1 || height <= 1)
    {
        return heightmap.Load(int3(0, 0, 0)).r * terrainData.heightScale;
    }

    float2 samplePosition = saturate(uv) * float2(width - 1, height - 1);
    int2 baseTexel = int2(floor(samplePosition));
    float2 fraction = samplePosition - float2(baseTexel);
    float rows[4];
    [unroll]
    for (int row = 0; row < 4; row++)
    {
        int y = clamp(baseTexel.y + row - 1, 0, int(height) - 1);
        float samples[4];
        [unroll]
        for (int column = 0; column < 4; column++)
        {
            int x = clamp(baseTexel.x + column - 1, 0, int(width) - 1);
            samples[column] = heightmap.Load(int3(x, y, 0)).r;
        }

        rows[row] = DdgiTerrainCubicHermite(samples[0], samples[1], samples[2], samples[3], fraction.x);
    }

    return saturate(DdgiTerrainCubicHermite(rows[0], rows[1], rows[2], rows[3], fraction.y)) * terrainData.heightScale;
}

float2 DdgiGetTerrainWorldSize(GpuInstanceData instanceData)
{
    float sizeX = abs(instanceData.terrainHeightmapUvScaleOffset.x) > 1e-5f
        ? instanceData.terrainChunkOriginSize.z / instanceData.terrainHeightmapUvScaleOffset.x
        : instanceData.terrainChunkOriginSize.z;
    float sizeZ = abs(instanceData.terrainHeightmapUvScaleOffset.y) > 1e-5f
        ? instanceData.terrainChunkOriginSize.w / instanceData.terrainHeightmapUvScaleOffset.y
        : instanceData.terrainChunkOriginSize.w;
    return float2(max(sizeX, 0.001f), max(sizeZ, 0.001f));
}

float3 DdgiComputeTerrainLocalNormal(float2 uv, GpuInstanceData instanceData, GpuTerrainMaterialData terrainData)
{
    Texture2D<float4> heightmap = GetTexture(terrainData.heightmapHandle);
    uint width = 1;
    uint height = 1;
    heightmap.GetDimensions(width, height);
    float2 texel = float2(
        width > 1 ? 1.0f / (width - 1) : 0.0f,
        height > 1 ? 1.0f / (height - 1) : 0.0f);
    float left = DdgiSampleTerrainHeight(uv - float2(texel.x, 0.0f), terrainData);
    float right = DdgiSampleTerrainHeight(uv + float2(texel.x, 0.0f), terrainData);
    float down = DdgiSampleTerrainHeight(uv - float2(0.0f, texel.y), terrainData);
    float up = DdgiSampleTerrainHeight(uv + float2(0.0f, texel.y), terrainData);
    float2 terrainWorldSize = DdgiGetTerrainWorldSize(instanceData);
    float spacingX = terrainWorldSize.x / max(width - 1, 1);
    float spacingZ = terrainWorldSize.y / max(height - 1, 1);
    float3 tangentX = float3(max(spacingX * 2.0f, 0.001f), right - left, 0.0f);
    float3 tangentZ = float3(0.0f, up - down, max(spacingZ * 2.0f, 0.001f));
    return normalize(cross(tangentZ, tangentX));
}

float3 DdgiGetTriplanarWeights(float3 worldNormal)
{
    float3 weights = pow(abs(normalize(worldNormal)), 4.0f);
    float sum = weights.x + weights.y + weights.z;
    return sum > 1e-5f ? weights / sum : float3(0.0f, 1.0f, 0.0f);
}

float4 DdgiSampleTriplanarFloat4(uint textureHandle, SamplerState samplerState, float3 worldPosition, float3 worldNormal, float layerScale)
{
    float3 weights = DdgiGetTriplanarWeights(worldNormal);
    float safeScale = max(layerScale, 0.001f);
    float4 sampleX = GetTexture(textureHandle).SampleLevel(samplerState, worldPosition.zy / safeScale, 0.0f);
    float4 sampleY = GetTexture(textureHandle).SampleLevel(samplerState, worldPosition.xz / safeScale, 0.0f);
    float4 sampleZ = GetTexture(textureHandle).SampleLevel(samplerState, worldPosition.xy / safeScale, 0.0f);
    return sampleX * weights.x + sampleY * weights.y + sampleZ * weights.z;
}

DdgiTerrainLayerSample DdgiSampleTerrainLayer(
    float3 worldPosition,
    float3 worldNormal,
    GpuTerrainLayerData layerData,
    GpuTerrainMaterialData terrainData)
{
    SamplerState samplerState = GetSampler(terrainData.layerSamplerHandle);
    DdgiTerrainLayerSample sample;
    sample.albedo = DdgiSampleTriplanarFloat4(layerData.albedoHandle, samplerState, worldPosition, worldNormal, layerData.scale);
    sample.height = layerData.hasHeight != 0u
        ? saturate(DdgiSampleTriplanarFloat4(layerData.heightHandle, samplerState, worldPosition, worldNormal, layerData.scale).r)
        : 0.5f;
    return sample;
}

float3 DdgiSampleTerrainAlbedo(float2 uv, float3 worldPosition, float3 worldNormal, GpuTerrainMaterialData terrainData)
{
    // Resolved exactly as the g-buffer pass does, terrain normal included, so a slope that renders
    // as cliff also bounces cliff albedo instead of whatever layer zero happens to be.
    TerrainLayerBlend blend = SampleTerrainLayerBlend(
        GetTexture(terrainData.layerIndexMapHandle),
        GetTexture(terrainData.layerWeightMapHandle),
        g_TerrainLayerTable,
        uv,
        worldNormal,
        terrainData);
    blend.weights = MaskInactiveTerrainLayers(blend.weights, terrainData.layerCount);

    DdgiTerrainLayerSample layerSamples[4];
    [unroll]
    for (uint i = 0u; i < 4u; i++)
    {
        layerSamples[i].albedo = float4(0.0f, 0.0f, 0.0f, 1.0f);
        layerSamples[i].height = 0.5f;
        uint layerIndex = min(blend.indices[i], max(terrainData.layerCount, 1u) - 1u);
        if (blend.weights[i] <= 0.0f || layerIndex >= terrainData.layerCount)
        {
            continue;
        }

        GpuTerrainLayerData layerData = g_TerrainLayerTable[terrainData.layerStart + layerIndex];
        layerSamples[i] = DdgiSampleTerrainLayer(worldPosition, worldNormal, layerData, terrainData);
    }

    float4 heights = float4(
        layerSamples[0].height,
        layerSamples[1].height,
        layerSamples[2].height,
        layerSamples[3].height);
    float4 weights = ResolveTerrainHeightBlendWeights(
        blend.weights,
        heights,
        terrainData.heightBlendSharpness);

    float4 albedo = layerSamples[0].albedo * weights.x +
        layerSamples[1].albedo * weights.y +
        layerSamples[2].albedo * weights.z +
        layerSamples[3].albedo * weights.w;
    return max(albedo.rgb, 0.0f.xxx);
}

bool DdgiTryResolveTerrainHitSurface(
    uint instanceIndex,
    uint primitiveIndex,
    float2 barycentrics,
    float fallbackRayT,
    float3 rayOrigin,
    float3 rayDirection,
    GpuInstanceData instanceData,
    out DdgiHitSurface surface)
{
    surface = (DdgiHitSurface)0;
    uint resolution = instanceIndex < WOLF_MAX_INSTANCE_COUNT ? g_InstanceIndexToTerrainRtResolution[instanceIndex] : 0u;
    if (resolution == 0u)
    {
        return false;
    }

    uint quadIndex = primitiveIndex / 2u;
    if (quadIndex >= resolution * resolution)
    {
        return false;
    }

    uint quadX = quadIndex % resolution;
    uint quadZ = quadIndex / resolution;
    float2 uv00 = float2(float(quadX), float(quadZ)) / float(resolution);
    float2 uv10 = float2(float(quadX + 1u), float(quadZ)) / float(resolution);
    float2 uv01 = float2(float(quadX), float(quadZ + 1u)) / float(resolution);
    float2 uv11 = float2(float(quadX + 1u), float(quadZ + 1u)) / float(resolution);
    float b1 = barycentrics.x;
    float b2 = barycentrics.y;
    float b0 = 1.0f - b1 - b2;
    float2 baseUv = (primitiveIndex & 1u) == 0u
        ? uv00 * b0 + uv01 * b1 + uv10 * b2
        : uv10 * b0 + uv01 * b1 + uv11 * b2;
    surface.uv = instanceData.terrainHeightmapUvScaleOffset.zw + baseUv * instanceData.terrainHeightmapUvScaleOffset.xy;

    uint materialIndex = DdgiResolveMaterialIndex(instanceData.materialHandle);
    GpuTerrainMaterialData terrainData = g_TerrainMaterialTable[materialIndex];
    if (terrainData.heightmapHandle == errorTextureHandle ||
        (terrainData.hasLayerMaps != 0u &&
            (terrainData.layerIndexMapHandle == errorTextureHandle ||
             terrainData.layerWeightMapHandle == errorTextureHandle)))
    {
        return false;
    }

    uint activeLayerCount = min(max(terrainData.layerCount, 1u), 4u);
    for (uint layerIndex = 0u; layerIndex < activeLayerCount; layerIndex++)
    {
        GpuTerrainLayerData layerData = g_TerrainLayerTable[terrainData.layerStart + layerIndex];
        if (layerData.albedoHandle == errorTextureHandle ||
            (layerData.hasHeight != 0u && layerData.heightHandle == errorTextureHandle))
        {
            return false;
        }
    }

    float3 localNormal = DdgiComputeTerrainLocalNormal(surface.uv, instanceData, terrainData);
    surface.worldPosition = rayOrigin + rayDirection * fallbackRayT;
    surface.worldNormal = normalize(mul(float4(localNormal, 0.0f), instanceData.world).xyz);
    surface.worldGeometricNormal = surface.worldNormal;
    surface.albedo = DdgiSampleTerrainAlbedo(surface.uv, surface.worldPosition, surface.worldNormal, terrainData);
    surface.emissive = 0.0f.xxx;
    surface.valid = true;
    return true;
}

bool DdgiTryResolveInstance(uint instanceIndex, out GpuInstanceData instanceData)
{
    instanceData = (GpuInstanceData)0;
    if (sidecarHitShadingAvailable == 0u || instanceIndex >= WOLF_MAX_INSTANCE_COUNT)
    {
        return false;
    }

    uint instanceHandle = g_InstanceIndexToInstanceHandle[instanceIndex];
    if (!IsPackedHandleValid(instanceHandle))
    {
        return false;
    }

    uint drawInstanceIndex = GetHandleIndex(instanceHandle);
    if (drawInstanceIndex == 0u || drawInstanceIndex >= WOLF_MAX_INSTANCE_COUNT)
    {
        return false;
    }

    instanceData = g_InstanceTable[drawInstanceIndex];
    return true;
}

bool DdgiTryResolveGeometricNormal(
    uint instanceIndex,
    uint primitiveIndex,
    float2 barycentrics,
    out float3 worldGeometricNormal)
{
    worldGeometricNormal = 0.0f.xxx;
    GpuInstanceData instanceData;
    if (!DdgiTryResolveInstance(instanceIndex, instanceData))
    {
        return false;
    }

    if (instanceData.drawKind == WOLF_DRAW_KIND_TERRAIN)
    {
        uint resolution = instanceIndex < WOLF_MAX_INSTANCE_COUNT
            ? g_InstanceIndexToTerrainRtResolution[instanceIndex]
            : 0u;
        uint quadIndex = primitiveIndex / 2u;
        if (resolution == 0u || quadIndex >= resolution * resolution)
        {
            return false;
        }

        uint quadX = quadIndex % resolution;
        uint quadZ = quadIndex / resolution;
        float2 uv00 = float2(float(quadX), float(quadZ)) / float(resolution);
        float2 uv10 = float2(float(quadX + 1u), float(quadZ)) / float(resolution);
        float2 uv01 = float2(float(quadX), float(quadZ + 1u)) / float(resolution);
        float2 uv11 = float2(float(quadX + 1u), float(quadZ + 1u)) / float(resolution);
        float b1 = barycentrics.x;
        float b2 = barycentrics.y;
        float b0 = 1.0f - b1 - b2;
        float2 baseUv = (primitiveIndex & 1u) == 0u
            ? uv00 * b0 + uv01 * b1 + uv10 * b2
            : uv10 * b0 + uv01 * b1 + uv11 * b2;
        float2 uv = instanceData.terrainHeightmapUvScaleOffset.zw +
            baseUv * instanceData.terrainHeightmapUvScaleOffset.xy;
        uint materialIndex = DdgiResolveMaterialIndex(instanceData.materialHandle);
        GpuTerrainMaterialData terrainData = g_TerrainMaterialTable[materialIndex];
        float3 localNormal = DdgiComputeTerrainLocalNormal(uv, instanceData, terrainData);
        worldGeometricNormal = normalize(mul(float4(localNormal, 0.0f), instanceData.world).xyz);
        return length(worldGeometricNormal) > 1e-4f;
    }

    if (!IsPackedHandleValid(instanceData.meshHandle))
    {
        return false;
    }

    uint meshIndex = GetHandleIndex(instanceData.meshHandle);
    if (meshIndex == 0u)
    {
        return false;
    }

    GpuMeshData meshData = g_MeshTable[meshIndex];
    uint firstIndex = primitiveIndex * 3u;
    if (meshData.indexCount == 0u || firstIndex + 2u >= meshData.indexCount)
    {
        return false;
    }

    uint i0 = g_PackedIndices[meshData.startIndex + firstIndex + 0u] + meshData.baseVertex;
    uint i1 = g_PackedIndices[meshData.startIndex + firstIndex + 1u] + meshData.baseVertex;
    uint i2 = g_PackedIndices[meshData.startIndex + firstIndex + 2u] + meshData.baseVertex;
    float3 worldPosition0 = mul(
        float4(GetPackedVertexPosition(g_PackedVertices[i0]), 1.0f), instanceData.world).xyz;
    float3 worldPosition1 = mul(
        float4(GetPackedVertexPosition(g_PackedVertices[i1]), 1.0f), instanceData.world).xyz;
    float3 worldPosition2 = mul(
        float4(GetPackedVertexPosition(g_PackedVertices[i2]), 1.0f), instanceData.world).xyz;
    worldGeometricNormal = normalize(cross(
        worldPosition1 - worldPosition0,
        worldPosition2 - worldPosition0));
    return length(worldGeometricNormal) > 1e-4f;
}

bool DdgiTryResolveHitSurface(
    uint instanceIndex,
    uint primitiveIndex,
    float2 barycentrics,
    float fallbackRayT,
    float3 rayOrigin,
    float3 rayDirection,
    out DdgiHitSurface surface)
{
    surface = (DdgiHitSurface)0;

    GpuInstanceData instanceData;
    if (!DdgiTryResolveInstance(instanceIndex, instanceData))
    {
        return false;
    }

    if (instanceData.drawKind == WOLF_DRAW_KIND_TERRAIN)
    {
        return DdgiTryResolveTerrainHitSurface(
            instanceIndex,
            primitiveIndex,
            barycentrics,
            fallbackRayT,
            rayOrigin,
            rayDirection,
            instanceData,
            surface);
    }

    if (IsPackedHandleValid(instanceData.meshHandle))
    {
        uint meshIndex = GetHandleIndex(instanceData.meshHandle);
        if (meshIndex > 0u)
        {
            GpuMeshData meshData = g_MeshTable[meshIndex];
            uint firstIndex = primitiveIndex * 3u;
            if (meshData.indexCount == 0u || firstIndex + 2u >= meshData.indexCount)
            {
                return false;
            }

            uint i0 = g_PackedIndices[meshData.startIndex + firstIndex + 0u] + meshData.baseVertex;
            uint i1 = g_PackedIndices[meshData.startIndex + firstIndex + 1u] + meshData.baseVertex;
            uint i2 = g_PackedIndices[meshData.startIndex + firstIndex + 2u] + meshData.baseVertex;
            PackedMeshVertex v0 = g_PackedVertices[i0];
            PackedMeshVertex v1 = g_PackedVertices[i1];
            PackedMeshVertex v2 = g_PackedVertices[i2];
            float b1 = barycentrics.x;
            float b2 = barycentrics.y;
            float b0 = 1.0f - b1 - b2;
            float3 localPosition =
                GetPackedVertexPosition(v0) * b0 +
                GetPackedVertexPosition(v1) * b1 +
                GetPackedVertexPosition(v2) * b2;
            float3 localNormal = normalize(
                GetPackedVertexNormal(v0) * b0 +
                GetPackedVertexNormal(v1) * b1 +
                GetPackedVertexNormal(v2) * b2);
            surface.uv =
                GetPackedVertexUv(v0) * b0 +
                GetPackedVertexUv(v1) * b1 +
                GetPackedVertexUv(v2) * b2;
            float3 worldPosition0 = mul(float4(GetPackedVertexPosition(v0), 1.0f), instanceData.world).xyz;
            float3 worldPosition1 = mul(float4(GetPackedVertexPosition(v1), 1.0f), instanceData.world).xyz;
            float3 worldPosition2 = mul(float4(GetPackedVertexPosition(v2), 1.0f), instanceData.world).xyz;
            surface.worldPosition = mul(float4(localPosition, 1.0f), instanceData.world).xyz;
            surface.worldNormal = normalize(mul(float4(localNormal, 0.0f), instanceData.world).xyz);
            surface.worldGeometricNormal = normalize(cross(
                worldPosition1 - worldPosition0,
                worldPosition2 - worldPosition0));
        }
    }

    if (length(surface.worldNormal) <= 1e-4f)
    {
        surface.worldPosition = rayOrigin + rayDirection * fallbackRayT;
        surface.worldNormal = -rayDirection;
    }
    if (length(surface.worldGeometricNormal) <= 1e-4f)
    {
        surface.worldGeometricNormal = surface.worldNormal;
    }

    uint materialIndex = DdgiResolveMaterialIndex(instanceData.materialHandle);

    GpuMaterialData material = g_MaterialTable[materialIndex];
    SamplerState materialSampler = GetSampler(material.samplerHandle);
    float3 albedoSample = GetTexture(material.albedoHandle).SampleLevel(materialSampler, surface.uv, 0.0f).rgb;
    float3 emissiveSample = GetTexture(material.emissiveHandle).SampleLevel(materialSampler, surface.uv, 0.0f).rgb;
    surface.albedo = max(material.baseColor.rgb * albedoSample, 0.0f.xxx);
    surface.emissive =
        emissiveSample *
        material.emissiveFactorIntensity.rgb *
        material.emissiveFactorIntensity.a;
    surface.valid = true;
    return true;
}

float DdgiTraceDirectionalLightVisibility(float3 worldPosition, float3 worldNormal, float3 lightDirection)
{
    if (length(directLightColorIntensity) <= 1e-5f || length(lightDirection) <= 1e-5f)
    {
        return 0.0f;
    }

    RayDesc ray;
    ray.Origin = worldPosition + worldNormal * max(normalBias, 0.001f);
    ray.Direction = lightDirection;
    ray.TMin = 0.01f;
    ray.TMax = 10000.0f;

    return WolfTraceAny(sceneTlas, ray, 0xFF).hit ? 0.0f : 1.0f;
}

float3 DdgiShadeHitSurface(DdgiHitSurface surface)
{
    float3 lightDirection = length(directLightDirection) > 1e-5f ? -normalize(directLightDirection) : 0.0f.xxx;
    float directWeight = saturate(dot(surface.worldNormal, lightDirection));
    float visibility = directWeight > 0.0f ? DdgiTraceDirectionalLightVisibility(surface.worldPosition, surface.worldNormal, lightDirection) : 0.0f;
    float3 directRadiance = directLightColorIntensity * directWeight * visibility / WOLF_PI;
    float3 recursiveRadiance =
        DdgiSampleHistory(surface.worldPosition, surface.worldNormal) *
        saturate(recursiveBounceEnergy);
    return surface.albedo * (directRadiance + recursiveRadiance) + surface.emissive;
}

DdgiTraceResult DdgiTraceRay(
    float3 originPosition,
    float3 rayDirection)
{
    RayDesc ray;
    ray.Origin = originPosition + rayDirection * normalBias;
    ray.Direction = rayDirection;
    ray.TMin = 0.001f;
    ray.TMax = maxRayDistance;

    WolfRayHit hit = WolfTraceClosest(sceneTlas, ray, 0xFF);

    DdgiTraceResult result;
    result.hitGeometry = false;
    result.backfaceHit = false;
    if (hit.hit)
    {
        result.hitGeometry = true;
        result.hitDistance = hit.distance;
        DdgiHitSurface surface;
        if (DdgiTryResolveHitSurface(
            hit.instanceIndex,
            hit.primitiveIndex,
            hit.barycentrics,
            result.hitDistance,
            ray.Origin,
            rayDirection,
            surface))
        {
            result.backfaceHit = dot(surface.worldGeometricNormal, rayDirection) > 0.0f;
            // Relocation must use geometric facing,
            // not material or interpolated normals used for lighting.
            result.radiance = result.backfaceHit
                ? 0.0f.xxx
                : DdgiShadeHitSurface(surface);
        }
        else
        {
            result.radiance = 0.8f.xxx;
        }
    }
    else
    {
        result.hitDistance = maxRayDistance;
        result.radiance = DdgiSampleSky(rayDirection);
    }

    return result;
}

DdgiTraceResult DdgiTraceGeometryRay(float3 originPosition, float3 rayDirection)
{
    RayDesc ray;
    // Relocation distances are measured from the probe center. Applying the
    // surface ray bias here can move the origin across nearby thin geometry.
    ray.Origin = originPosition;
    ray.Direction = rayDirection;
    ray.TMin = 0.001f;
    ray.TMax = maxRayDistance;

    WolfRayHit hit = WolfTraceClosest(sceneTlas, ray, 0xFF);

    DdgiTraceResult result = (DdgiTraceResult)0;
    result.hitDistance = maxRayDistance;
    if (!hit.hit)
    {
        return result;
    }

    result.hitGeometry = true;
    result.hitDistance = hit.distance;
    float3 worldGeometricNormal;
    if (DdgiTryResolveGeometricNormal(
        hit.instanceIndex,
        hit.primitiveIndex,
        hit.barycentrics,
        worldGeometricNormal))
    {
        result.backfaceHit = dot(worldGeometricNormal, rayDirection) > 0.0f;
    }
    return result;
}

groupshared uint g_ProbeActive;

[numthreads(16, 1, 1)]
void DdgiRelocationTraceCS(
    uint3 groupId : SV_GroupID,
    uint3 groupThreadId : SV_GroupThreadID,
    uint groupIndex : SV_GroupIndex)
{
    uint probeIndex = groupId.x;
    if (probeIndex >= probeCount)
    {
        return;
    }

    if (groupIndex == 0u)
    {
        g_ProbeActive = DdgiIsProbeRelocationActive(probeIndex) ? 1u : 0u;
    }
    GroupMemoryBarrierWithGroupSync();

    if (g_ProbeActive == 0u)
    {
        return;
    }

    uint rayIndex = groupThreadId.x;
    bool sourceAvailable = DdgiProbeHasHistory(probeIndex);
    float3 direction = DdgiRelocationRayDirection(rayIndex);
    float3 probePosition = DdgiRelocatedProbePosition(
        probeIndex,
        probeRelocationStateReadHandle,
        sourceAvailable);
    DdgiTraceResult result = DdgiTraceGeometryRay(probePosition, direction);
    uint2 pixel = DdgiRaySamplePixel(probeIndex, rayIndex, visibilityTileInteriorSize);
    RWTexture2D<float4> target = GetRWTexture(traceVisibilityHandle);
    target[pixel] = float4(
        result.hitDistance,
        0.0f,
        result.hitGeometry ? 1.0f : 0.0f,
        result.backfaceHit ? 1.0f : 0.0f);
}

[numthreads(64, 1, 1)]
void DdgiProbeTraceCS(
    uint3 groupId : SV_GroupID,
    uint3 groupThreadId : SV_GroupThreadID,
    uint groupIndex : SV_GroupIndex)
{
    uint probeIndex = groupId.x;
    if (probeIndex >= probeCount)
    {
        return;
    }

    if (groupIndex == 0u)
    {
        g_ProbeActive = DdgiIsProbeUpdateActive(probeIndex) ? 1u : 0u;
    }
    GroupMemoryBarrierWithGroupSync();
    if (g_ProbeActive == 0u)
    {
        return;
    }

    uint visibilityRayCount = DdgiRaySampleCount(visibilityTileInteriorSize);
    uint irradianceRayCount = DdgiRaySampleCount(irradianceTileInteriorSize);
    bool sharedRaySet = visibilityRayCount == irradianceRayCount;
    float3 probePosition = DdgiCurrentProbePosition(probeIndex);
    RWTexture2D<float4> visibilityTarget = GetRWTexture(traceVisibilityHandle);
    RWTexture2D<float4> irradianceTarget = GetRWTexture(traceIrradianceHandle);

    for (uint rayIndex = groupThreadId.x; rayIndex < visibilityRayCount; rayIndex += 64u)
    {
        float3 direction = DdgiRayDirection(rayIndex, visibilityRayCount, probeIndex);
        DdgiTraceResult result = DdgiTraceRay(probePosition, direction);
        float visibilityDistance = result.backfaceHit
            ? result.hitDistance * 0.9f
            : result.hitDistance;
        float normalizedDistance = saturate(visibilityDistance / max(maxRayDistance, 0.001f));
        uint2 visibilityPixel = DdgiRaySamplePixel(
            probeIndex,
            rayIndex,
            visibilityTileInteriorSize);
        visibilityTarget[visibilityPixel] = float4(
            normalizedDistance,
            normalizedDistance * normalizedDistance,
            result.hitGeometry ? 1.0f : 0.0f,
            result.backfaceHit ? 1.0f : 0.0f);

        if (sharedRaySet)
        {
            uint2 irradiancePixel = DdgiRaySamplePixel(
                probeIndex,
                rayIndex,
                irradianceTileInteriorSize);
            irradianceTarget[irradiancePixel] = float4(result.radiance, 1.0f);
        }
    }

    if (!sharedRaySet && groupThreadId.x < irradianceRayCount)
    {
        uint rayIndex = groupThreadId.x;
        float3 direction = DdgiRayDirection(rayIndex, irradianceRayCount, probeIndex);
        DdgiTraceResult result = DdgiTraceRay(probePosition, direction);
        uint2 irradiancePixel = DdgiRaySamplePixel(
            probeIndex,
            rayIndex,
            irradianceTileInteriorSize);
        irradianceTarget[irradiancePixel] = float4(result.radiance, 1.0f);
    }
}
