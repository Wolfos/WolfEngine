import "../Common/common.slang";
import "../Common/common_bindless.slang";
import "../Common/common_lighting.slang";
import "../Common/draw_data.slang";
import "../Common/packed_vertex.slang";
import "../Common/raytracing_common.slang";

#define WOLF_MAX_INSTANCE_COUNT 65535

cbuffer BindlessHandles : register(b0)
{
    uint depthHandle;
    uint normalHandle;
    uint outputHandle;
    uint hitMaskHandle;
    uint hitDistanceHandle;
    uint albedoHandle;
};

cbuffer CameraParams : register(b1)
{
    CameraParams camera;
};

cbuffer AoSettings : register(b2)
{
    uint fullResolutionX;
    uint fullResolutionY;
    uint outputResolutionX;
    uint outputResolutionY;
    float radius;
    float bias;
    float strength;
    uint frameIndex;
    uint debugOutputsEnabled;
};

RaytracingAccelerationStructure sceneTlas : register(t3);
StructuredBuffer<GpuInstanceData> g_InstanceTable : register(t4);
StructuredBuffer<GpuMaterialData> g_MaterialTable : register(t5);
StructuredBuffer<uint> g_InstanceIndexToInstanceHandle : register(t6);
StructuredBuffer<GpuMeshData> g_MeshTable : register(t7);
StructuredBuffer<uint> g_PackedIndices : register(t9);

StructuredBuffer<PackedMeshVertex> g_PackedMeshVertices : register(t8);

float Hash12(uint2 pixel, uint frame)
{
    uint state = pixel.x * 747796405u + pixel.y * 2891336453u + frame * 277803737u + 0x9E3779B9u;
    state = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    state = (state >> 22u) ^ state;
    return float(state) / 4294967295.0f;
}

float3 BuildHemisphereDirection(float3 normal, float randomAngle)
{
    float3 helper = abs(normal.y) < 0.999f ? float3(0.0f, 1.0f, 0.0f) : float3(1.0f, 0.0f, 0.0f);
    float3 tangent = normalize(cross(helper, normal));
    float3 bitangent = cross(normal, tangent);
    float angle = randomAngle * 6.28318530718f;
    float r = 0.70710678f;
    return normalize(normal * r + tangent * cos(angle) * r + bitangent * sin(angle) * r);
}

struct RtaoRayQueryResult
{
    bool hit;
    float committedRayT;
};

RtaoRayQueryResult TraceRtaoRay(float3 worldPosition, float3 normal, uint2 pixel)
{
    float3 rayDirection = BuildHemisphereDirection(normal, Hash12(pixel, frameIndex));

    RayDesc ray;
    ray.Origin = worldPosition + normal * bias;
    ray.Direction = rayDirection;
    ray.TMin = 0.001f;
    ray.TMax = radius;

    WolfRayAnyHit hit = WolfTraceAny(sceneTlas, ray, 0xFF);

    RtaoRayQueryResult result;
    result.hit = hit.hit;
    result.committedRayT = hit.distance;
    return result;
}

void WriteDebugOutputs(uint2 pixel, bool hit, float committedRayT)
{
    if (debugOutputsEnabled == 0u)
    {
        return;
    }

    RWTexture2D<float4> hitMaskTexture = GetRWTexture(hitMaskHandle);
    RWTexture2D<float4> hitDistanceTexture = GetRWTexture(hitDistanceHandle);
    float hitMask = hit ? 1.0f : 0.0f;
    float hitDistance = hit ? saturate(committedRayT / radius) : 1.0f;
    hitMaskTexture[pixel] = float4(hitMask, hitMask, hitMask, 1.0f);
    hitDistanceTexture[pixel] = float4(hitDistance, hitDistance, hitDistance, 1.0f);
}

float3 TracePrimaryAlbedo(uint2 pixel, uint2 fullSize)
{
    float2 uv = (float2(pixel) + 0.5f) / float2(fullSize);
    float4 clipPosition = float4(uv.x * 2.0f - 1.0f, -(uv.y * 2.0f - 1.0f), 1.0f, 1.0f);
    float4 farPositionH = mul(clipPosition, camera.invViewProjection);
    float3 rayDirection = normalize(farPositionH.xyz / farPositionH.w);

    RayDesc ray;
    ray.Origin = camera.cameraOrigin;
    ray.Direction = rayDirection;
    ray.TMin = 0.001f;
    ray.TMax = 100000.0f;

    WolfRayHit hit = WolfTraceClosest(sceneTlas, ray, 0xFF);
    if (!hit.hit)
    {
        return 0.0f.xxx;
    }

    uint instanceIndex = hit.instanceIndex;
    if (instanceIndex >= WOLF_MAX_INSTANCE_COUNT)
    {
        return 1.0f.xxx;
    }
    uint instanceHandle = g_InstanceIndexToInstanceHandle[instanceIndex];
    if (!IsPackedHandleValid(instanceHandle))
    {
        return 1.0f.xxx;
    }
    GpuInstanceData instanceData = g_InstanceTable[GetHandleIndex(instanceHandle)];
    if (!IsPackedHandleValid(instanceData.meshHandle))
    {
        return 1.0f.xxx;
    }
    uint meshIndex = GetHandleIndex(instanceData.meshHandle);
    GpuMeshData meshData = g_MeshTable[meshIndex];
    uint firstIndex = hit.primitiveIndex * 3u;
    if (firstIndex + 2u >= meshData.indexCount)
    {
        return 1.0f.xxx;
    }

    uint i0 = g_PackedIndices[meshData.startIndex + firstIndex + 0u] + meshData.baseVertex;
    uint i1 = g_PackedIndices[meshData.startIndex + firstIndex + 1u] + meshData.baseVertex;
    uint i2 = g_PackedIndices[meshData.startIndex + firstIndex + 2u] + meshData.baseVertex;
    PackedMeshVertex v0 = g_PackedMeshVertices[i0];
    PackedMeshVertex v1 = g_PackedMeshVertices[i1];
    PackedMeshVertex v2 = g_PackedMeshVertices[i2];
    float b1 = hit.barycentrics.x;
    float b2 = hit.barycentrics.y;
    float b0 = 1.0f - b1 - b2;
    float2 hitUv =
        GetPackedVertexUv(v0) * b0 +
        GetPackedVertexUv(v1) * b1 +
        GetPackedVertexUv(v2) * b2;

    uint materialIndex = GetHandleIndex(instanceData.materialHandle);
    GpuMaterialData material = g_MaterialTable[materialIndex];
    return max(material.baseColor.rgb * GetTexture(material.albedoHandle).SampleLevel(
        GetSampler(material.samplerHandle), hitUv, 0.0f).rgb, 0.0f.xxx);
}

[numthreads(8, 8, 1)]
void AmbientOcclusionRayTracedCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 pixel = dispatchThreadId.xy;
    uint2 outputSize = uint2(outputResolutionX, outputResolutionY);
    uint2 fullSize = uint2(fullResolutionX, fullResolutionY);
    RWTexture2D<float4> albedoTexture = GetRWTexture(albedoHandle);
    if (pixel.x >= outputSize.x || pixel.y >= outputSize.y)
    {
        if (pixel.x < fullSize.x && pixel.y < fullSize.y)
        {
            albedoTexture[pixel] = float4(TracePrimaryAlbedo(pixel, fullSize), 1.0f);
        }
        return;
    }

    Texture2D<float4> depthTexture = GetTexture(depthHandle);
    Texture2D<float4> normalTexture = GetTexture(normalHandle);
    RWTexture2D<float4> outputTexture = GetRWTexture(outputHandle);

    uint2 sourcePixel = uint2((float2(pixel) + 0.5f) / float2(outputSize) * float2(fullSize));
    sourcePixel = min(sourcePixel, fullSize - 1u);

    float depth = depthTexture.Load(int3(sourcePixel, 0)).r;
    if (IsBackgroundDepth(depth))
    {
        outputTexture[pixel] = float4(1.0f, 1.0f, 1.0f, 1.0f);
        WriteDebugOutputs(pixel, false, radius);
        albedoTexture[pixel] = float4(TracePrimaryAlbedo(sourcePixel, fullSize), 1.0f);
        return;
    }

    float3 normal = DecodeNormal(normalTexture.Load(int3(sourcePixel, 0)).xyz);
    float4 clipPosition = float4(
        (float(sourcePixel.x) + 0.5f) / float(fullSize.x) * 2.0f - 1.0f,
        -((float(sourcePixel.y) + 0.5f) / float(fullSize.y) * 2.0f - 1.0f),
        depth,
        1.0f);
    float4 cameraRelativeWorldPositionH = mul(clipPosition, camera.invViewProjection);
    float3 cameraRelativeWorldPosition = cameraRelativeWorldPositionH.xyz / cameraRelativeWorldPositionH.w;
    float3 worldPosition = cameraRelativeWorldPosition + camera.cameraOrigin;
    RtaoRayQueryResult rayResult = TraceRtaoRay(worldPosition, normal, pixel);
    float occlusion = rayResult.hit ? saturate(1.0f - strength) : 1.0f;
    WriteDebugOutputs(pixel, rayResult.hit, rayResult.committedRayT);
    albedoTexture[pixel] = float4(TracePrimaryAlbedo(sourcePixel, fullSize), 1.0f);
    outputTexture[pixel] = float4(occlusion, occlusion, occlusion, 1.0f);
}
