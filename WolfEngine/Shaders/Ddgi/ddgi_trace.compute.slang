import "../Common/common_bindless.slang";
import "../Common/draw_data.slang";
import "../Common/packed_vertex.slang";
import "../Common/raytracing_common.slang";
import "ddgi_common.slang";
import "../Common/rt_hit_surface.slang";

static const uint DDGI_STATIC_GEOMETRY_MASK = 0x02u;

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

RtSceneTables DdgiSceneTables()
{
    RtSceneTables tables;
    tables.instances = g_InstanceTable;
    tables.materials = g_MaterialTable;
    tables.instanceIndexToInstanceHandle = g_InstanceIndexToInstanceHandle;
    tables.meshes = g_MeshTable;
    tables.packedVertices = g_PackedVertices;
    tables.packedIndices = g_PackedIndices;
    tables.terrainMaterials = g_TerrainMaterialTable;
    tables.terrainLayers = g_TerrainLayerTable;
    tables.instanceIndexToTerrainRtResolution = g_InstanceIndexToTerrainRtResolution;
    tables.errorTextureHandle = errorTextureHandle;
    return tables;
}

bool DdgiTryResolveGeometricNormal(
    uint instanceIndex,
    uint primitiveIndex,
    float2 barycentrics,
    out float3 worldGeometricNormal)
{
    worldGeometricNormal = 0.0f.xxx;
    if (sidecarHitShadingAvailable == 0u)
    {
        return false;
    }

    return RtTryResolveGeometricNormal(
        DdgiSceneTables(),
        instanceIndex,
        primitiveIndex,
        barycentrics,
        worldGeometricNormal);
}

bool DdgiTryResolveHitSurface(
    uint instanceIndex,
    uint primitiveIndex,
    float2 barycentrics,
    float fallbackRayT,
    float3 rayOrigin,
    float3 rayDirection,
    out RtHitSurface surface)
{
    surface = (RtHitSurface)0;
    if (sidecarHitShadingAvailable == 0u)
    {
        return false;
    }

    return RtTryResolveHitSurface(
        DdgiSceneTables(),
        instanceIndex,
        primitiveIndex,
        barycentrics,
        fallbackRayT,
        rayOrigin,
        rayDirection,
        surface);
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

float3 DdgiShadeHitSurface(RtHitSurface surface)
{
    float3 lightDirection = length(directLightDirection) > 1e-5f ? -normalize(directLightDirection) : 0.0f.xxx;
    float directWeight = saturate(dot(surface.worldNormal, lightDirection));
    float visibility = directWeight > 0.0f ? DdgiTraceDirectionalLightVisibility(surface.worldPosition, surface.worldNormal, lightDirection) : 0.0f;
    float3 directRadiance = directLightColorIntensity * directWeight * visibility / WOLF_PI;
    float3 recursiveRadiance =
        DdgiSampleHistory(surface.worldPosition, surface.worldNormal) *
        saturate(recursiveBounceEnergy);
    // Repeated probe projection, interpolation, and L1 reconstruction damp the
    // feedback signal at every bounce. Compensate that numerical loss without
    // allowing a surface's recursive throughput to reach one and run away.
    static const float DDGI_RECURSIVE_FEEDBACK_COMPENSATION = 4.0f;
    static const float DDGI_MAX_RECURSIVE_SURFACE_THROUGHPUT = 0.9f;
    float maximumSurfaceThroughput =
        max(surface.albedo.r, max(surface.albedo.g, surface.albedo.b)) *
        saturate(recursiveBounceEnergy);
    float recursiveCompensation = min(
        DDGI_RECURSIVE_FEEDBACK_COMPENSATION,
        DDGI_MAX_RECURSIVE_SURFACE_THROUGHPUT /
            max(maximumSurfaceThroughput, 1e-4f));
    return surface.albedo *
        (directRadiance + recursiveRadiance * recursiveCompensation) +
        surface.emissive;
}

DdgiTraceResult DdgiTraceRay(
    float3 originPosition,
    float3 rayDirection)
{
    RayDesc ray;
    // Keep ray distances in the probe's coordinate system. Moving the origin
    // forward made every visibility moment shorter than the receiver distance
    // measured during sampling, which falsely occluded surfaces close to a probe.
    ray.Origin = originPosition;
    ray.Direction = rayDirection;
    ray.TMin = max(normalBias, 0.001f);
    ray.TMax = maxRayDistance;

    WolfRayHit hit = WolfTraceClosest(sceneTlas, ray, 0xFF);

    DdgiTraceResult result;
    result.hitGeometry = false;
    result.backfaceHit = false;
    if (hit.hit)
    {
        result.hitGeometry = true;
        result.hitDistance = hit.distance;
        RtHitSurface surface;
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

    WolfRayHit hit = WolfTraceClosest(sceneTlas, ray, DDGI_STATIC_GEOMETRY_MASK);

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
    float3 probePosition = DdgiCurrentProbePosition(probeIndex);
    RWTexture2D<float4> visibilityTarget = GetRWTexture(traceVisibilityHandle);
    RWTexture2D<float4> irradianceTarget = GetRWTexture(traceIrradianceHandle);

    for (uint rayIndex = groupThreadId.x; rayIndex < visibilityRayCount; rayIndex += 64u)
    {
        float3 direction = DdgiVisibilityRayDirection(rayIndex, visibilityRayCount, probeIndex);
        // Visibility rays only need geometry. Keep stochastic, shaded irradiance
        // rays separate even when the two sets contain the same number of rays.
        DdgiTraceResult result = DdgiTraceGeometryRay(probePosition, direction);
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
    }

    if (groupThreadId.x < irradianceRayCount)
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
