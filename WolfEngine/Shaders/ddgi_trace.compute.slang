import "common_bindless.slang";
import "draw_data.slang";
import "ddgi_common.slang";

#define WOLF_MAX_INSTANCE_COUNT 65535
#define WOLF_MAX_MATERIAL_COUNT 65535

RaytracingAccelerationStructure sceneTlas : register(t3);
StructuredBuffer<GpuInstanceData> g_InstanceTable : register(t4);
StructuredBuffer<GpuMaterialData> g_MaterialTable : register(t5);
StructuredBuffer<uint> g_InstanceIndexToInstanceHandle : register(t6);
StructuredBuffer<GpuMeshData> g_MeshTable : register(t7);

struct DdgiTraceResult
{
    float3 radiance;
    float hitDistance;
};

float3 DdgiResolveHitRadiance(uint instanceIndex, float3 rayDirection)
{
    if (sidecarHitShadingAvailable == 0u || instanceIndex >= WOLF_MAX_INSTANCE_COUNT)
    {
        return 0.8f.xxx;
    }

    uint instanceHandle = g_InstanceIndexToInstanceHandle[instanceIndex];
    if (!IsPackedHandleValid(instanceHandle))
    {
        return 0.8f.xxx;
    }

    uint drawInstanceIndex = GetHandleIndex(instanceHandle);
    if (drawInstanceIndex == 0u || drawInstanceIndex >= WOLF_MAX_INSTANCE_COUNT)
    {
        return 0.8f.xxx;
    }

    GpuInstanceData instanceData = g_InstanceTable[drawInstanceIndex];
    if (IsPackedHandleValid(instanceData.meshHandle))
    {
        uint meshIndex = GetHandleIndex(instanceData.meshHandle);
        if (meshIndex > 0u)
        {
            GpuMeshData meshData = g_MeshTable[meshIndex];
            if (meshData.indexCount == 0u)
            {
                return 0.8f.xxx;
            }
        }
    }

    uint materialHandle = instanceData.materialHandle;
    if (!IsPackedHandleValid(materialHandle))
    {
        return 0.8f.xxx;
    }

    uint materialIndex = GetHandleIndex(materialHandle);
    if (materialIndex == 0u || materialIndex >= WOLF_MAX_MATERIAL_COUNT)
    {
        return 0.8f.xxx;
    }

    GpuMaterialData material = g_MaterialTable[materialIndex];
    float3 albedo = max(material.baseColor.rgb, 0.02f.xxx);
    float3 emissive = material.emissiveFactorIntensity.rgb * material.emissiveFactorIntensity.a;
    float3 approximateHitNormal = -rayDirection;
    float3 lightDirection = -normalize(directLightDirection);
    float directWeight = saturate(dot(approximateHitNormal, lightDirection));
    float3 directRadiance = directLightColorIntensity * (0.15f + directWeight);
    float3 ambientRadiance = DdgiSampleSky(approximateHitNormal) * 0.25f;
    return albedo * (directRadiance + ambientRadiance) + emissive;
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
        result.radiance = DdgiResolveHitRadiance(query.CommittedInstanceIndex(), rayDirection);
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
