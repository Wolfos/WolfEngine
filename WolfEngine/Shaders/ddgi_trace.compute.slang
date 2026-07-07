import "common_bindless.slang";
import "draw_data.slang";
import "ddgi_common.slang";

#define WOLF_MAX_INSTANCE_COUNT 65535
#define WOLF_MAX_MATERIAL_COUNT 65535
#define WOLF_DRAW_KIND_TERRAIN 2u

struct DdgiPackedVertex
{
    float4 position;
    float3 normal;
    float2 uv;
    float4 tangent;
};

RaytracingAccelerationStructure sceneTlas : register(t3);
StructuredBuffer<GpuInstanceData> g_InstanceTable : register(t4);
StructuredBuffer<GpuMaterialData> g_MaterialTable : register(t5);
StructuredBuffer<uint> g_InstanceIndexToInstanceHandle : register(t6);
StructuredBuffer<GpuMeshData> g_MeshTable : register(t7);
StructuredBuffer<DdgiPackedVertex> g_PackedVertices : register(t8);
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

struct DdgiTerrainLayerBlend
{
    uint4 indices;
    float4 weights;
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

void DdgiAddTerrainLayerContribution(inout uint4 indices, inout float4 weights, uint layerIndex, float contribution)
{
    if (contribution <= 0.0f)
    {
        return;
    }

    [unroll]
    for (uint slot = 0u; slot < 4u; slot++)
    {
        if (weights[slot] > 0.0f && indices[slot] == layerIndex)
        {
            weights[slot] += contribution;
            return;
        }
    }

    uint minSlot = 0u;
    float minWeight = weights.x;
    [unroll]
    for (uint slot = 0u; slot < 4u; slot++)
    {
        if (weights[slot] <= 0.0f)
        {
            indices[slot] = layerIndex;
            weights[slot] = contribution;
            return;
        }
        if (weights[slot] < minWeight)
        {
            minWeight = weights[slot];
            minSlot = slot;
        }
    }

    if (contribution > minWeight)
    {
        indices[minSlot] = layerIndex;
        weights[minSlot] = contribution;
    }
}

void DdgiAccumulateTerrainLayerMapTexel(
    Texture2D<float4> indexMap,
    Texture2D<float4> weightMap,
    uint2 texel,
    float bilinearWeight,
    inout uint4 indices,
    inout float4 weights)
{
    float4 indexSample = indexMap.Load(int3(texel, 0));
    float4 weightSample = saturate(weightMap.Load(int3(texel, 0))) * bilinearWeight;
    uint4 texelIndices = uint4(round(saturate(indexSample) * 255.0f));
    DdgiAddTerrainLayerContribution(indices, weights, texelIndices.x, weightSample.x);
    DdgiAddTerrainLayerContribution(indices, weights, texelIndices.y, weightSample.y);
    DdgiAddTerrainLayerContribution(indices, weights, texelIndices.z, weightSample.z);
    DdgiAddTerrainLayerContribution(indices, weights, texelIndices.w, weightSample.w);
}

DdgiTerrainLayerBlend DdgiSampleTerrainLayerBlend(float2 uv, GpuTerrainMaterialData terrainData)
{
    DdgiTerrainLayerBlend blend;
    blend.indices = uint4(0, 0, 0, 0);
    blend.weights = float4(1.0f, 0.0f, 0.0f, 0.0f);
    if (terrainData.hasLayerMaps == 0)
    {
        return blend;
    }

    Texture2D<float4> indexMap = GetTexture(terrainData.layerIndexMapHandle);
    uint indexWidth = 1;
    uint indexHeight = 1;
    indexMap.GetDimensions(indexWidth, indexHeight);
    float2 samplePosition = saturate(uv) * float2(indexWidth, indexHeight) - 0.5f;
    float2 basePosition = floor(samplePosition);
    float2 fraction = saturate(samplePosition - basePosition);
    int2 maxTexel = int2(max(indexWidth, 1u) - 1u, max(indexHeight, 1u) - 1u);
    int2 texel00 = clamp(int2(basePosition), int2(0, 0), maxTexel);
    int2 texel10 = clamp(texel00 + int2(1, 0), int2(0, 0), maxTexel);
    int2 texel01 = clamp(texel00 + int2(0, 1), int2(0, 0), maxTexel);
    int2 texel11 = clamp(texel00 + int2(1, 1), int2(0, 0), maxTexel);

    blend.indices = uint4(0, 0, 0, 0);
    blend.weights = float4(0.0f, 0.0f, 0.0f, 0.0f);
    Texture2D<float4> weightMap = GetTexture(terrainData.layerWeightMapHandle);
    DdgiAccumulateTerrainLayerMapTexel(indexMap, weightMap, uint2(texel00), (1.0f - fraction.x) * (1.0f - fraction.y), blend.indices, blend.weights);
    DdgiAccumulateTerrainLayerMapTexel(indexMap, weightMap, uint2(texel10), fraction.x * (1.0f - fraction.y), blend.indices, blend.weights);
    DdgiAccumulateTerrainLayerMapTexel(indexMap, weightMap, uint2(texel01), (1.0f - fraction.x) * fraction.y, blend.indices, blend.weights);
    DdgiAccumulateTerrainLayerMapTexel(indexMap, weightMap, uint2(texel11), fraction.x * fraction.y, blend.indices, blend.weights);
    return blend;
}

float4 DdgiNormalizeTerrainWeights(float4 weights)
{
    float sum = weights.x + weights.y + weights.z + weights.w;
    return sum > 1e-5f ? weights / sum : float4(1.0f, 0.0f, 0.0f, 0.0f);
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
    DdgiTerrainLayerBlend blend = DdgiSampleTerrainLayerBlend(uv, terrainData);
    if (terrainData.layerCount < 4u) blend.weights.w = 0.0f;
    if (terrainData.layerCount < 3u) blend.weights.z = 0.0f;
    if (terrainData.layerCount < 2u) blend.weights.y = 0.0f;

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

    float4 biased = blend.weights;
    biased.x *= pow(max(layerSamples[0].height, 0.0001f), max(terrainData.heightBlendSharpness, 0.0001f));
    biased.y *= pow(max(layerSamples[1].height, 0.0001f), max(terrainData.heightBlendSharpness, 0.0001f));
    biased.z *= pow(max(layerSamples[2].height, 0.0001f), max(terrainData.heightBlendSharpness, 0.0001f));
    biased.w *= pow(max(layerSamples[3].height, 0.0001f), max(terrainData.heightBlendSharpness, 0.0001f));
    float4 weights = DdgiNormalizeTerrainWeights(biased);

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
    float3 worldPosition0 = mul(g_PackedVertices[i0].position, instanceData.world).xyz;
    float3 worldPosition1 = mul(g_PackedVertices[i1].position, instanceData.world).xyz;
    float3 worldPosition2 = mul(g_PackedVertices[i2].position, instanceData.world).xyz;
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
            DdgiPackedVertex v0 = g_PackedVertices[i0];
            DdgiPackedVertex v1 = g_PackedVertices[i1];
            DdgiPackedVertex v2 = g_PackedVertices[i2];
            float b1 = barycentrics.x;
            float b2 = barycentrics.y;
            float b0 = 1.0f - b1 - b2;
            float3 localPosition =
                v0.position.xyz * b0 +
                v1.position.xyz * b1 +
                v2.position.xyz * b2;
            float3 localNormal = normalize(v0.normal * b0 + v1.normal * b1 + v2.normal * b2);
            surface.uv =
                v0.uv * b0 +
                v1.uv * b1 +
                v2.uv * b2;
            float3 worldPosition0 = mul(v0.position, instanceData.world).xyz;
            float3 worldPosition1 = mul(v1.position, instanceData.world).xyz;
            float3 worldPosition2 = mul(v2.position, instanceData.world).xyz;
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

    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER> query;
    query.TraceRayInline(sceneTlas, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER, 0xFF, ray);
    query.Proceed();
    return query.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? 0.0f : 1.0f;
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

    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER> query;
    query.TraceRayInline(sceneTlas, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER, 0xFF, ray);
    query.Proceed();

    DdgiTraceResult result;
    result.hitGeometry = false;
    result.backfaceHit = false;
    if (query.CommittedStatus() == COMMITTED_TRIANGLE_HIT)
    {
        result.hitGeometry = true;
        result.hitDistance = query.CommittedRayT();
        DdgiHitSurface surface;
        if (DdgiTryResolveHitSurface(
            query.CommittedInstanceIndex(),
            query.CommittedPrimitiveIndex(),
            query.CommittedTriangleBarycentrics(),
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

    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER> query;
    query.TraceRayInline(
        sceneTlas,
        RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER,
        0xFF,
        ray);
    query.Proceed();

    DdgiTraceResult result = (DdgiTraceResult)0;
    result.hitDistance = maxRayDistance;
    if (query.CommittedStatus() != COMMITTED_TRIANGLE_HIT)
    {
        return result;
    }

    result.hitGeometry = true;
    result.hitDistance = query.CommittedRayT();
    float3 worldGeometricNormal;
    if (DdgiTryResolveGeometricNormal(
        query.CommittedInstanceIndex(),
        query.CommittedPrimitiveIndex(),
        query.CommittedTriangleBarycentrics(),
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
