import "common_bindless.slang";
import "draw_data.slang";
import "packed_vertex.slang";
import "../Terrain/terrain_layer_blend.slang";

// Resolves a hardware ray hit into the surface a diffuse bounce needs: position, shading and
// geometric normals, albedo and emissive, for both meshes and terrain. Shared by every pass that
// shades diffuse ray hits so they agree on what a hit looks like; when one copy learned terrain and
// another did not, the same wall bounced different light depending on who traced it.
//
// Deliberately declares no registers. Each pass binds the scene tables to its own registers and
// hands them over as an RtSceneTables value.

static const uint RT_MAX_INSTANCE_COUNT = 65535u;
static const uint RT_MAX_MATERIAL_COUNT = 65535u;
static const uint RT_DRAW_KIND_TERRAIN = 2u;

struct RtSceneTables
{
    StructuredBuffer<GpuInstanceData> instances;
    StructuredBuffer<GpuMaterialData> materials;
    StructuredBuffer<uint> instanceIndexToInstanceHandle;
    StructuredBuffer<GpuMeshData> meshes;
    StructuredBuffer<PackedMeshVertex> packedVertices;
    StructuredBuffer<uint> packedIndices;
    StructuredBuffer<GpuTerrainMaterialData> terrainMaterials;
    StructuredBuffer<GpuTerrainLayerData> terrainLayers;
    StructuredBuffer<uint> instanceIndexToTerrainRtResolution;
    uint errorTextureHandle;
};

struct RtHitSurface
{
    bool valid;
    float3 worldPosition;
    float3 worldNormal;
    float3 worldGeometricNormal;
    float2 uv;
    float3 albedo;
    float3 emissive;
};

struct RtTerrainLayerSample
{
    float4 albedo;
    float height;
};

/// Normalizes, or returns zero for a vector too short to have a direction. Plain normalize turns a
/// degenerate triangle's zero-length cross product or cancelling vertex normals into NaN, and NaN slips
/// past every later length check; one such hit used to poison whatever accumulated it. Callers test
/// for the zero result and fall back.
float3 RtSafeNormalize(float3 value)
{
    float lengthSquared = dot(value, value);
    return lengthSquared > 1e-20f ? value * rsqrt(lengthSquared) : 0.0f.xxx;
}

uint RtResolveMaterialIndex(uint materialHandle)
{
    if (!IsPackedHandleValid(materialHandle))
    {
        return 0u;
    }

    uint materialIndex = GetHandleIndex(materialHandle);
    return materialIndex > 0u && materialIndex < RT_MAX_MATERIAL_COUNT ? materialIndex : 0u;
}

float RtTerrainCubicHermite(float a, float b, float c, float d, float t)
{
    return b + 0.5f * t * (c - a + t * (2.0f * a - 5.0f * b + 4.0f * c - d + t * (3.0f * (b - c) + d - a)));
}

float RtSampleTerrainHeight(float2 uv, GpuTerrainMaterialData terrainData)
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

        rows[row] = RtTerrainCubicHermite(samples[0], samples[1], samples[2], samples[3], fraction.x);
    }

    return saturate(RtTerrainCubicHermite(rows[0], rows[1], rows[2], rows[3], fraction.y)) * terrainData.heightScale;
}

float2 RtGetTerrainWorldSize(GpuInstanceData instanceData)
{
    float sizeX = abs(instanceData.terrainHeightmapUvScaleOffset.x) > 1e-5f
        ? instanceData.terrainChunkOriginSize.z / instanceData.terrainHeightmapUvScaleOffset.x
        : instanceData.terrainChunkOriginSize.z;
    float sizeZ = abs(instanceData.terrainHeightmapUvScaleOffset.y) > 1e-5f
        ? instanceData.terrainChunkOriginSize.w / instanceData.terrainHeightmapUvScaleOffset.y
        : instanceData.terrainChunkOriginSize.w;
    return float2(max(sizeX, 0.001f), max(sizeZ, 0.001f));
}

float3 RtComputeTerrainLocalNormal(float2 uv, GpuInstanceData instanceData, GpuTerrainMaterialData terrainData)
{
    Texture2D<float4> heightmap = GetTexture(terrainData.heightmapHandle);
    uint width = 1;
    uint height = 1;
    heightmap.GetDimensions(width, height);
    float2 texel = float2(
        width > 1 ? 1.0f / (width - 1) : 0.0f,
        height > 1 ? 1.0f / (height - 1) : 0.0f);
    float left = RtSampleTerrainHeight(uv - float2(texel.x, 0.0f), terrainData);
    float right = RtSampleTerrainHeight(uv + float2(texel.x, 0.0f), terrainData);
    float down = RtSampleTerrainHeight(uv - float2(0.0f, texel.y), terrainData);
    float up = RtSampleTerrainHeight(uv + float2(0.0f, texel.y), terrainData);
    float2 terrainWorldSize = RtGetTerrainWorldSize(instanceData);
    float spacingX = terrainWorldSize.x / max(width - 1, 1);
    float spacingZ = terrainWorldSize.y / max(height - 1, 1);
    float3 tangentX = float3(max(spacingX * 2.0f, 0.001f), right - left, 0.0f);
    float3 tangentZ = float3(0.0f, up - down, max(spacingZ * 2.0f, 0.001f));
    return RtSafeNormalize(cross(tangentZ, tangentX));
}

float3 RtGetTriplanarWeights(float3 worldNormal)
{
    float3 weights = pow(abs(normalize(worldNormal)), 4.0f);
    float sum = weights.x + weights.y + weights.z;
    return sum > 1e-5f ? weights / sum : float3(0.0f, 1.0f, 0.0f);
}

float4 RtSampleTriplanarFloat4(uint textureHandle, SamplerState samplerState, float3 worldPosition, float3 worldNormal, float layerScale)
{
    float3 weights = RtGetTriplanarWeights(worldNormal);
    float safeScale = max(layerScale, 0.001f);
    float4 sampleX = GetTexture(textureHandle).SampleLevel(samplerState, worldPosition.zy / safeScale, 0.0f);
    float4 sampleY = GetTexture(textureHandle).SampleLevel(samplerState, worldPosition.xz / safeScale, 0.0f);
    float4 sampleZ = GetTexture(textureHandle).SampleLevel(samplerState, worldPosition.xy / safeScale, 0.0f);
    return sampleX * weights.x + sampleY * weights.y + sampleZ * weights.z;
}

RtTerrainLayerSample RtSampleTerrainLayer(
    float3 worldPosition,
    float3 worldNormal,
    GpuTerrainLayerData layerData,
    GpuTerrainMaterialData terrainData)
{
    SamplerState samplerState = GetSampler(terrainData.layerSamplerHandle);
    RtTerrainLayerSample sample;
    sample.albedo = RtSampleTriplanarFloat4(layerData.albedoHandle, samplerState, worldPosition, worldNormal, layerData.scale);
    sample.height = layerData.hasHeight != 0u
        ? saturate(RtSampleTriplanarFloat4(layerData.heightHandle, samplerState, worldPosition, worldNormal, layerData.scale).r)
        : 0.5f;
    return sample;
}

float3 RtSampleTerrainAlbedo(
    RtSceneTables tables,
    float2 uv,
    float3 worldPosition,
    float3 worldNormal,
    GpuTerrainMaterialData terrainData)
{
    // Resolved exactly as the g-buffer pass does, terrain normal included, so a slope that renders
    // as cliff also bounces cliff albedo instead of whatever layer zero happens to be.
    TerrainLayerBlend blend = SampleTerrainLayerBlend(
        GetTexture(terrainData.layerIndexMapHandle),
        GetTexture(terrainData.layerWeightMapHandle),
        tables.terrainLayers,
        uv,
        worldNormal,
        terrainData);
    blend.weights = MaskInactiveTerrainLayers(blend.weights, terrainData.layerCount);

    RtTerrainLayerSample layerSamples[4];
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

        GpuTerrainLayerData layerData = tables.terrainLayers[terrainData.layerStart + layerIndex];
        layerSamples[i] = RtSampleTerrainLayer(worldPosition, worldNormal, layerData, terrainData);
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

/// Heightmap uv of a terrain hit, reconstructed from the quad grid the terrain BLAS was built from.
bool RtTryResolveTerrainUv(
    RtSceneTables tables,
    uint instanceIndex,
    uint primitiveIndex,
    float2 barycentrics,
    GpuInstanceData instanceData,
    out float2 uv)
{
    uv = 0.0f.xx;
    uint resolution = instanceIndex < RT_MAX_INSTANCE_COUNT
        ? tables.instanceIndexToTerrainRtResolution[instanceIndex]
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
    uv = instanceData.terrainHeightmapUvScaleOffset.zw + baseUv * instanceData.terrainHeightmapUvScaleOffset.xy;
    return true;
}

bool RtTryResolveTerrainHitSurface(
    RtSceneTables tables,
    uint instanceIndex,
    uint primitiveIndex,
    float2 barycentrics,
    float fallbackRayT,
    float3 rayOrigin,
    float3 rayDirection,
    GpuInstanceData instanceData,
    out RtHitSurface surface)
{
    surface = (RtHitSurface)0;
    if (!RtTryResolveTerrainUv(tables, instanceIndex, primitiveIndex, barycentrics, instanceData, surface.uv))
    {
        return false;
    }

    uint materialIndex = RtResolveMaterialIndex(instanceData.materialHandle);
    GpuTerrainMaterialData terrainData = tables.terrainMaterials[materialIndex];
    if (terrainData.heightmapHandle == tables.errorTextureHandle ||
        (terrainData.hasLayerMaps != 0u &&
            (terrainData.layerIndexMapHandle == tables.errorTextureHandle ||
             terrainData.layerWeightMapHandle == tables.errorTextureHandle)))
    {
        return false;
    }

    uint activeLayerCount = min(max(terrainData.layerCount, 1u), 4u);
    for (uint layerIndex = 0u; layerIndex < activeLayerCount; layerIndex++)
    {
        GpuTerrainLayerData layerData = tables.terrainLayers[terrainData.layerStart + layerIndex];
        if (layerData.albedoHandle == tables.errorTextureHandle ||
            (layerData.hasHeight != 0u && layerData.heightHandle == tables.errorTextureHandle))
        {
            return false;
        }
    }

    float3 localNormal = RtComputeTerrainLocalNormal(surface.uv, instanceData, terrainData);
    surface.worldPosition = rayOrigin + rayDirection * fallbackRayT;
    surface.worldNormal = RtSafeNormalize(mul(float4(localNormal, 0.0f), instanceData.world).xyz);
    if (length(surface.worldNormal) <= 1e-4f)
    {
        return false;
    }
    surface.worldGeometricNormal = surface.worldNormal;
    surface.albedo = RtSampleTerrainAlbedo(tables, surface.uv, surface.worldPosition, surface.worldNormal, terrainData);
    surface.emissive = 0.0f.xxx;
    surface.valid = true;
    return true;
}

bool RtTryResolveInstance(RtSceneTables tables, uint instanceIndex, out GpuInstanceData instanceData)
{
    instanceData = (GpuInstanceData)0;
    if (instanceIndex >= RT_MAX_INSTANCE_COUNT)
    {
        return false;
    }

    uint instanceHandle = tables.instanceIndexToInstanceHandle[instanceIndex];
    if (!IsPackedHandleValid(instanceHandle))
    {
        return false;
    }

    uint drawInstanceIndex = GetHandleIndex(instanceHandle);
    if (drawInstanceIndex == 0u || drawInstanceIndex >= RT_MAX_INSTANCE_COUNT)
    {
        return false;
    }

    instanceData = tables.instances[drawInstanceIndex];
    return true;
}

bool RtTryResolveGeometricNormal(
    RtSceneTables tables,
    uint instanceIndex,
    uint primitiveIndex,
    float2 barycentrics,
    out float3 worldGeometricNormal)
{
    worldGeometricNormal = 0.0f.xxx;
    GpuInstanceData instanceData;
    if (!RtTryResolveInstance(tables, instanceIndex, instanceData))
    {
        return false;
    }

    if (instanceData.drawKind == RT_DRAW_KIND_TERRAIN)
    {
        float2 uv;
        if (!RtTryResolveTerrainUv(tables, instanceIndex, primitiveIndex, barycentrics, instanceData, uv))
        {
            return false;
        }

        uint materialIndex = RtResolveMaterialIndex(instanceData.materialHandle);
        GpuTerrainMaterialData terrainData = tables.terrainMaterials[materialIndex];
        float3 localNormal = RtComputeTerrainLocalNormal(uv, instanceData, terrainData);
        worldGeometricNormal = RtSafeNormalize(mul(float4(localNormal, 0.0f), instanceData.world).xyz);
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

    GpuMeshData meshData = tables.meshes[meshIndex];
    uint firstIndex = primitiveIndex * 3u;
    if (meshData.indexCount == 0u || firstIndex + 2u >= meshData.indexCount)
    {
        return false;
    }

    uint i0 = tables.packedIndices[meshData.startIndex + firstIndex + 0u] + meshData.baseVertex;
    uint i1 = tables.packedIndices[meshData.startIndex + firstIndex + 1u] + meshData.baseVertex;
    uint i2 = tables.packedIndices[meshData.startIndex + firstIndex + 2u] + meshData.baseVertex;
    float3 worldPosition0 = mul(
        float4(GetPackedVertexPosition(tables.packedVertices[i0]), 1.0f), instanceData.world).xyz;
    float3 worldPosition1 = mul(
        float4(GetPackedVertexPosition(tables.packedVertices[i1]), 1.0f), instanceData.world).xyz;
    float3 worldPosition2 = mul(
        float4(GetPackedVertexPosition(tables.packedVertices[i2]), 1.0f), instanceData.world).xyz;
    worldGeometricNormal = RtSafeNormalize(cross(
        worldPosition1 - worldPosition0,
        worldPosition2 - worldPosition0));
    return length(worldGeometricNormal) > 1e-4f;
}

bool RtTryResolveHitSurface(
    RtSceneTables tables,
    uint instanceIndex,
    uint primitiveIndex,
    float2 barycentrics,
    float fallbackRayT,
    float3 rayOrigin,
    float3 rayDirection,
    out RtHitSurface surface)
{
    surface = (RtHitSurface)0;

    GpuInstanceData instanceData;
    if (!RtTryResolveInstance(tables, instanceIndex, instanceData))
    {
        return false;
    }

    if (instanceData.drawKind == RT_DRAW_KIND_TERRAIN)
    {
        return RtTryResolveTerrainHitSurface(
            tables,
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
            GpuMeshData meshData = tables.meshes[meshIndex];
            uint firstIndex = primitiveIndex * 3u;
            if (meshData.indexCount == 0u || firstIndex + 2u >= meshData.indexCount)
            {
                return false;
            }

            uint i0 = tables.packedIndices[meshData.startIndex + firstIndex + 0u] + meshData.baseVertex;
            uint i1 = tables.packedIndices[meshData.startIndex + firstIndex + 1u] + meshData.baseVertex;
            uint i2 = tables.packedIndices[meshData.startIndex + firstIndex + 2u] + meshData.baseVertex;
            PackedMeshVertex v0 = tables.packedVertices[i0];
            PackedMeshVertex v1 = tables.packedVertices[i1];
            PackedMeshVertex v2 = tables.packedVertices[i2];
            float b1 = barycentrics.x;
            float b2 = barycentrics.y;
            float b0 = 1.0f - b1 - b2;
            float3 localPosition =
                GetPackedVertexPosition(v0) * b0 +
                GetPackedVertexPosition(v1) * b1 +
                GetPackedVertexPosition(v2) * b2;
            float3 localNormal = RtSafeNormalize(
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
            surface.worldNormal = RtSafeNormalize(mul(float4(localNormal, 0.0f), instanceData.world).xyz);
            surface.worldGeometricNormal = RtSafeNormalize(cross(
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

    uint materialIndex = RtResolveMaterialIndex(instanceData.materialHandle);

    GpuMaterialData material = tables.materials[materialIndex];
    SamplerState materialSampler = GetSampler(material.samplerHandle);
    float2 materialUv = surface.uv * material.uvOffsetScale.zw + material.uvOffsetScale.xy;
    float3 albedoSample = GetTexture(material.albedoHandle).SampleLevel(materialSampler, materialUv, 0.0f).rgb;
    float3 emissiveSample = GetTexture(material.emissiveHandle).SampleLevel(materialSampler, materialUv, 0.0f).rgb;
    surface.albedo = max(material.baseColor.rgb * albedoSample, 0.0f.xxx);
    surface.emissive =
        emissiveSample *
        material.emissiveFactorIntensity.rgb *
        material.emissiveFactorIntensity.a;
    surface.valid = true;
    return true;
}
