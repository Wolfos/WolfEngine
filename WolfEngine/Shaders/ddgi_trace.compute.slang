import "common_bindless.slang";
import "draw_data.slang";
import "ddgi_common.slang";

#define WOLF_MAX_INSTANCE_COUNT 65535
#define WOLF_MAX_MATERIAL_COUNT 65535

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

struct DdgiTraceResult
{
    float3 radiance;
    float hitDistance;
};

struct DdgiHitSurface
{
    bool valid;
    float3 worldPosition;
    float3 worldNormal;
    float3 albedo;
    float3 emissive;
};

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
            surface.worldPosition = mul(float4(localPosition, 1.0f), instanceData.world).xyz;
            surface.worldNormal = normalize(mul(float4(localNormal, 0.0f), instanceData.world).xyz);
        }
    }

    if (length(surface.worldNormal) <= 1e-4f)
    {
        surface.worldPosition = rayOrigin + rayDirection * fallbackRayT;
        surface.worldNormal = -rayDirection;
    }

    uint materialHandle = instanceData.materialHandle;
    if (!IsPackedHandleValid(materialHandle))
    {
        surface.albedo = 0.8f.xxx;
        surface.emissive = 0.0f.xxx;
        surface.valid = true;
        return true;
    }

    uint materialIndex = GetHandleIndex(materialHandle);
    if (materialIndex == 0u || materialIndex >= WOLF_MAX_MATERIAL_COUNT)
    {
        surface.albedo = 0.8f.xxx;
        surface.emissive = 0.0f.xxx;
        surface.valid = true;
        return true;
    }

    GpuMaterialData material = g_MaterialTable[materialIndex];
    surface.albedo = max(material.baseColor.rgb, 0.02f.xxx);
    surface.emissive = material.emissiveFactorIntensity.rgb * material.emissiveFactorIntensity.a;
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
    float3 directRadiance = directLightColorIntensity * directWeight * visibility;
    float3 ambientRadiance = DdgiSampleSky(surface.worldNormal) * 0.08f;
    return surface.albedo * (directRadiance + ambientRadiance) + surface.emissive;
}

DdgiTraceResult DdgiTraceRay(float3 originPosition, float3 direction, uint2 pixel, uint probeIndex)
{
    float3 rayDirection = DdgiJitterDirection(direction, pixel, probeIndex);
    RayDesc ray;
    ray.Origin = originPosition + rayDirection * normalBias;
    ray.Direction = rayDirection;
    ray.TMin = 0.001f;
    ray.TMax = maxRayDistance;

    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER> query;
    query.TraceRayInline(sceneTlas, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER, 0xFF, ray);
    query.Proceed();

    DdgiTraceResult result;
    if (query.CommittedStatus() == COMMITTED_TRIANGLE_HIT)
    {
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
            result.radiance = DdgiShadeHitSurface(surface);
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

void TraceIrradianceSample(uint2 pixel)
{
    DdgiTexelInfo info = DdgiDecodeAtlasTexel(pixel, irradianceTileInteriorSize);
    RWTexture2D<float4> target = GetRWTexture(traceIrradianceHandle);
    if (!info.valid)
    {
        target[pixel] = 0.0f.xxxx;
        return;
    }

    uint rayIndex = DdgiRaySampleIndex(info.interiorPixel, irradianceTileInteriorSize);
    uint rayCount = DdgiRaySampleCount(irradianceTileInteriorSize);
    if (rayIndex >= rayCount)
    {
        target[pixel] = 0.0f.xxxx;
        return;
    }

    float3 direction = DdgiRayDirection(rayIndex, rayCount, info.probeIndex);
    DdgiTraceResult result = DdgiTraceRay(DdgiProbePosition(info.probeIndex), direction, pixel, info.probeIndex);
    target[pixel] = float4(result.radiance, 1.0f);
}

void TraceVisibilitySample(uint2 pixel)
{
    DdgiTexelInfo info = DdgiDecodeAtlasTexel(pixel, visibilityTileInteriorSize);
    RWTexture2D<float4> target = GetRWTexture(traceVisibilityHandle);
    if (!info.valid)
    {
        target[pixel] = float4(1.0f, 1.0f, 0.0f, 1.0f);
        return;
    }

    uint rayIndex = DdgiRaySampleIndex(info.interiorPixel, visibilityTileInteriorSize);
    uint rayCount = DdgiRaySampleCount(visibilityTileInteriorSize);
    if (rayIndex >= rayCount)
    {
        target[pixel] = float4(1.0f, 1.0f, 0.0f, 1.0f);
        return;
    }

    float3 direction = DdgiRayDirection(rayIndex, rayCount, info.probeIndex);
    DdgiTraceResult result = DdgiTraceRay(DdgiProbePosition(info.probeIndex), direction, pixel, info.probeIndex);
    float normalizedDistance = saturate(result.hitDistance / max(maxRayDistance, 0.001f));
    target[pixel] = float4(normalizedDistance, normalizedDistance * normalizedDistance, 0.0f, 1.0f);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 pixel = dispatchThreadId.xy;
    if (pixel.x < irradianceAtlasWidth && pixel.y < irradianceAtlasHeight)
    {
        TraceIrradianceSample(pixel);
    }

    if (pixel.x < visibilityAtlasWidth && pixel.y < visibilityAtlasHeight)
    {
        TraceVisibilitySample(pixel);
    }
}
