import "../Common/common.slang";
import "../Common/constants.slang";
import "../Common/common_bindless.slang";
import "../Common/common_lighting.slang";
import "../Common/draw_data.slang";
import "../Common/normal_decode.slang";
import "../Common/packed_vertex.slang";
import "../Common/raytracing_common.slang";
import "reflections_common.slang";

#define WOLF_MAX_INSTANCE_COUNT 65535

cbuffer BindlessHandles : register(b0)
{
    uint depthHandle;
    uint normalHandle;
    uint materialHandle;
    uint velocityHandle;
    uint environmentHandle;
    uint irradianceHandle;
    uint prefilteredHandle;
    uint brdfLutHandle;
    uint outputHandle;
    uint samplerHandle;
};

cbuffer CameraParams : register(b1)
{
    CameraParams camera;
    row_major float4x4 viewProjection;
};

cbuffer ReflectionSettings : register(b2)
{
    uint framebufferSizeX;
    uint framebufferSizeY;
    uint maxSteps;
    uint binarySearchSteps;
    float maxRayDistance;
    float thickness;
    float bias;
    float maxRoughness;
    float edgeFade;
    float intensity;
    uint directionalLightCount;
    uint colorPyramidLevelCount;
    uint colorPyramidValid;
    float screenReuseFalloff;
    float reprojectionStrength;
    uint outputSizeX;
    uint outputSizeY;
    float2 reflectionSettingsPadding;
    uint colorPyramidHandles[REFLECTION_MAX_COLOR_PYRAMID_LEVELS];
    DirectionalLightData directionalLights[MAX_DIRECTIONAL_LIGHTS];
};

RaytracingAccelerationStructure sceneTlas : register(t3);
StructuredBuffer<GpuInstanceData> g_InstanceTable : register(t4);
StructuredBuffer<GpuMaterialData> g_MaterialTable : register(t5);
StructuredBuffer<uint> g_InstanceIndexToInstanceHandle : register(t6);
StructuredBuffer<GpuMeshData> g_MeshTable : register(t7);
StructuredBuffer<uint> g_PackedIndices : register(t9);

StructuredBuffer<PackedMeshVertex> g_PackedVertices : register(t8);

float3 ReconstructCameraRelativePosition(float2 uv, float depth)
{
    float4 clip = float4(uv.x * 2.0f - 1.0f, -(uv.y * 2.0f - 1.0f), depth, 1.0f);
    float4 positionH = mul(clip, camera.invViewProjection);
    return positionH.xyz / positionH.w;
}

// Only rejects positions behind the camera. Whether the projection landed inside the frame is
// left to an edge weight, so screen reuse ramps out at the borders instead of switching off.
bool ProjectWorldPosition(float3 worldPosition, out float2 uv)
{
    float3 cameraRelativePosition = worldPosition - camera.cameraOrigin;
    float4 clip = mul(float4(cameraRelativePosition, 1.0f), viewProjection);
    if (clip.w <= 1e-5f)
    {
        uv = 0.0f.xx;
        return false;
    }
    float2 ndc = clip.xy / clip.w;
    uv = float2(ndc.x * 0.5f + 0.5f, -ndc.y * 0.5f + 0.5f);
    return true;
}

// Reflection ray for an arbitrary pixel, used to measure how fast the reflected image moves
// across neighbouring pixels. Curvature makes this swing hard even at roughness zero.
bool ReflectionRayAt(uint2 pixel, uint2 size, out float3 worldOrigin, out float3 direction)
{
    worldOrigin = 0.0f.xxx;
    direction = 0.0f.xxx;
    float depth = GetTexture(depthHandle).Load(int3(pixel, 0)).r;
    if (IsBackgroundDepth(depth))
    {
        return false;
    }

    float2 uv = (float2(pixel) + 0.5f) / float2(size);
    float3 cameraRelativePosition = ReconstructCameraRelativePosition(uv, depth);
    float3 normal = DecodeNormal(GetTexture(normalHandle).Load(int3(pixel, 0)).xyz);
    float3 viewDirection = normalize(-cameraRelativePosition);
    worldOrigin = cameraRelativePosition + camera.cameraOrigin;
    direction = normalize(reflect(-viewDirection, normal));
    return true;
}

/// Where a neighbouring pixel's reflection ray would land at the same ray length. Falls back to
/// the centre uv when the neighbour is not a comparable surface, keeping silhouettes sharp.
float2 NeighbourReflectionHitUv(uint2 pixel, uint2 size, float rayLength, float2 fallbackUv)
{
    float3 worldOrigin;
    float3 direction;
    if (!ReflectionRayAt(pixel, size, worldOrigin, direction))
    {
        return fallbackUv;
    }

    float2 uv;
    if (!ProjectWorldPosition(worldOrigin + direction * rayLength, uv))
    {
        return fallbackUv;
    }

    return uv;
}

/// Weight with which the previous frame's shaded color can stand in for a ray hit, in 0..1.
/// Every term is a ramp rather than a test: a hit that is half-trusted contributes half its
/// screen color and half the freshly shaded material, so the transition between the two sources
/// is a gradient across the surface instead of a per-pixel coin flip that crawls as the camera
/// moves. Only genuinely unusable cases (no history, behind the camera, hit projects onto sky)
/// return zero outright.
float ComputeScreenReuseWeight(
    float3 hitPosition,
    float roughness,
    float rayLength,
    uint2 pixel,
    out float3 radiance)
{
    radiance = 0.0f.xxx;
    if (colorPyramidValid == 0u)
    {
        return 0.0f;
    }

    float2 uv;
    if (!ProjectWorldPosition(hitPosition, uv))
    {
        return 0.0f;
    }

    float edgeWeight = ReflectionScreenEdgeWeight(uv, edgeFade);
    if (edgeWeight <= 0.0f)
    {
        return 0.0f;
    }

    uint2 size = uint2(framebufferSizeX, framebufferSizeY);
    uint2 hitPixel = min(uint2(saturate(uv) * float2(size)), size - 1u);
    float sceneDepth = GetTexture(depthHandle).Load(int3(hitPixel, 0)).r;
    if (IsBackgroundDepth(sceneDepth))
    {
        return 0.0f;
    }

    // How well the depth buffer agrees that this hit is the surface visible at that pixel.
    // Fading over the upper half of the budget keeps thin geometry and grazing hits from
    // popping in and out between frames.
    float2 sceneUv = (float2(hitPixel) + 0.5f) / float2(size);
    float3 scenePosition = ReconstructCameraRelativePosition(sceneUv, sceneDepth) + camera.cameraOrigin;
    float allowedSeparation = thickness * (1.0f + length(scenePosition - camera.cameraOrigin) * 0.02f);
    float separationFadeStart = min(
        allowedSeparation * (1.0f - saturate(screenReuseFalloff)),
        allowedSeparation - 1e-5f);
    float separationWeight = 1.0f - smoothstep(
        separationFadeStart,
        allowedSeparation,
        distance(scenePosition, hitPosition));
    if (separationWeight <= 0.0f)
    {
        return 0.0f;
    }

    float footprintLevel = ReflectionFootprintLevel(
        uv,
        NeighbourReflectionHitUv(uint2(min(pixel.x + 1u, size.x - 1u), pixel.y), size, rayLength, uv),
        NeighbourReflectionHitUv(uint2(pixel.x, min(pixel.y + 1u, size.y - 1u)), size, rayLength, uv),
        size);

    // Reflections run ahead of deferred lighting, so the only shaded color available for a
    // visible hit is the previous frame's pyramid.
    float historyConfidence;
    radiance = SampleReflectionColorPyramid(
        colorPyramidHandles,
        colorPyramidLevelCount,
        samplerHandle,
        velocityHandle,
        size,
        uv,
        roughness,
        edgeFade,
        footprintLevel,
        reprojectionStrength,
        historyConfidence);
    return saturate(edgeWeight * separationWeight * historyConfidence);
}

float TraceDirectionalVisibility(float3 worldPosition, float3 worldNormal, float3 lightDirection)
{
    RayDesc shadowRay;
    shadowRay.Origin = worldPosition + worldNormal * max(bias, 0.002f);
    shadowRay.Direction = lightDirection;
    shadowRay.TMin = 0.002f;
    shadowRay.TMax = max(camera.farPlane, 1.0f);

    return WolfTraceAny(sceneTlas, shadowRay, 0xFF).hit ? 0.0f : 1.0f;
}

bool TryShadeHitMaterial(
    uint instanceIndex,
    uint primitiveIndex,
    float2 barycentrics,
    float3 hitPosition,
    float3 incomingRayDirection,
    out float3 radiance)
{
    radiance = 0.0f.xxx;
    if (instanceIndex >= WOLF_MAX_INSTANCE_COUNT)
    {
        return false;
    }

    uint instanceHandle = g_InstanceIndexToInstanceHandle[instanceIndex];
    if (!IsPackedHandleValid(instanceHandle))
    {
        return false;
    }
    GpuInstanceData instanceData = g_InstanceTable[GetHandleIndex(instanceHandle)];
    if (!IsPackedHandleValid(instanceData.meshHandle) || !IsPackedHandleValid(instanceData.materialHandle))
    {
        return false;
    }

    GpuMeshData mesh = g_MeshTable[GetHandleIndex(instanceData.meshHandle)];
    uint firstIndex = primitiveIndex * 3u;
    if (firstIndex + 2u >= mesh.indexCount)
    {
        return false;
    }

    uint i0 = g_PackedIndices[mesh.startIndex + firstIndex + 0u] + mesh.baseVertex;
    uint i1 = g_PackedIndices[mesh.startIndex + firstIndex + 1u] + mesh.baseVertex;
    uint i2 = g_PackedIndices[mesh.startIndex + firstIndex + 2u] + mesh.baseVertex;
    PackedMeshVertex v0 = g_PackedVertices[i0];
    PackedMeshVertex v1 = g_PackedVertices[i1];
    PackedMeshVertex v2 = g_PackedVertices[i2];
    float b0 = 1.0f - barycentrics.x - barycentrics.y;
    float2 uv =
        GetPackedVertexUv(v0) * b0 +
        GetPackedVertexUv(v1) * barycentrics.x +
        GetPackedVertexUv(v2) * barycentrics.y;
    float3 localNormal = normalize(
        GetPackedVertexNormal(v0) * b0 +
        GetPackedVertexNormal(v1) * barycentrics.x +
        GetPackedVertexNormal(v2) * barycentrics.y);
    float4 localTangent = normalize(
        v0.tangent * b0 +
        v1.tangent * barycentrics.x +
        v2.tangent * barycentrics.y);
    float3 worldPosition0 = mul(float4(GetPackedVertexPosition(v0), 1.0f), instanceData.world).xyz;
    float3 worldPosition1 = mul(float4(GetPackedVertexPosition(v1), 1.0f), instanceData.world).xyz;
    float3 worldPosition2 = mul(float4(GetPackedVertexPosition(v2), 1.0f), instanceData.world).xyz;
    float3 worldGeometricNormal = normalize(cross(
        worldPosition1 - worldPosition0,
        worldPosition2 - worldPosition0));
    if (dot(worldGeometricNormal, -incomingRayDirection) < 0.0f)
    {
        worldGeometricNormal = -worldGeometricNormal;
    }
    float3 worldNormal = normalize(mul(float4(localNormal, 0.0f), instanceData.world).xyz);
    float3 worldTangent =
        normalize(mul(float4(localTangent.xyz, 0.0f), instanceData.world).xyz);

    GpuMaterialData material = g_MaterialTable[GetHandleIndex(instanceData.materialHandle)];
    SamplerState materialSampler = GetSampler(material.samplerHandle);
    float4 albedoSample =
        GetTexture(material.albedoHandle).SampleLevel(materialSampler, uv, 0.0f);
    float4 ormSample =
        GetTexture(material.ormHandle).SampleLevel(materialSampler, uv, 0.0f);
    float3 tangentNormal = ApplyNormalMapScale(
        DecodeNormalMapSample(
            GetTexture(material.normalHandle).SampleLevel(materialSampler, uv, 0.0f)),
        material.metallicRoughness.w);
    float3 worldBitangent =
        normalize(cross(worldNormal, worldTangent) * localTangent.w);
    float3x3 tangentToWorld =
        float3x3(worldTangent, worldBitangent, worldNormal);
    worldNormal = normalize(mul(tangentNormal, tangentToWorld));
    if (dot(worldNormal, worldGeometricNormal) < 0.0f)
    {
        worldNormal = -worldNormal;
    }

    float3 albedo = max(material.baseColor.rgb * albedoSample.rgb, 0.0f.xxx);
    float3 emissive = material.emissiveFactorIntensity.rgb *
        material.emissiveFactorIntensity.a *
        GetTexture(material.emissiveHandle).SampleLevel(materialSampler, uv, 0.0f).rgb;

    SurfaceLightingInput surface = (SurfaceLightingInput)0;
    surface.worldPos = hitPosition;
    surface.normal = worldNormal;
    surface.albedo = albedo;
    surface.metallic = saturate(ormSample.b * material.metallicRoughness.x);
    surface.roughness = saturate(ormSample.g * material.metallicRoughness.y);
    surface.occlusion = saturate(ormSample.r);
    surface.emissive = emissive;
    float3 hitViewDirection = normalize(-incomingRayDirection);
    radiance = EvaluateImageBasedLighting(
        surface,
        hitViewDirection,
        irradianceHandle,
        prefilteredHandle,
        brdfLutHandle,
        samplerHandle,
        IBL_EXPOSURE);

    [unroll]
    for (uint lightIndex = 0u;
         lightIndex < directionalLightCount && lightIndex < MAX_DIRECTIONAL_LIGHTS;
         lightIndex++)
    {
        DirectionalLightData light = directionalLights[lightIndex];
        float3 lightDirection = -normalize(light.directionAndType.xyz);
        float nDotL = dot(worldNormal, lightDirection);
        if (nDotL <= 0.0f)
        {
            continue;
        }
        float visibility =
            TraceDirectionalVisibility(hitPosition, worldGeometricNormal, lightDirection);
        float3 lightColor = light.colorIntensity.rgb * light.colorIntensity.a;
        radiance += AccumulateLight(
            surface,
            hitViewDirection,
            lightColor,
            lightDirection,
            1.0f,
            visibility);
    }

    radiance = max(radiance + emissive, 0.0f.xxx);
    return true;
}

[numthreads(8, 8, 1)]
void ReflectionsRayTracedCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 outputPixel = dispatchThreadId.xy;
    uint2 outputSize = uint2(outputSizeX, outputSizeY);
    if (any(outputPixel >= outputSize))
    {
        return;
    }

    uint2 size = uint2(framebufferSizeX, framebufferSizeY);
    float2 fullPixelPosition =
        (float2(outputPixel) + 0.5f) * float2(size) / float2(outputSize);
    uint2 pixel = min(uint2(fullPixelPosition), size - 1u);

    RWTexture2D<float4> outputTexture = GetRWTexture(outputHandle);
    outputTexture[outputPixel] = 0.0f.xxxx;
    float depth = GetTexture(depthHandle).Load(int3(pixel, 0)).r;
    if (IsBackgroundDepth(depth))
    {
        return;
    }

    float4 material = GetTexture(materialHandle).Load(int3(pixel, 0));
    float roughness = material.g;
    float roughnessFade = ReflectionRoughnessFade(roughness, maxRoughness);
    if (roughnessFade <= 0.0f || intensity <= 0.0f)
    {
        return;
    }

    float2 screenUv = (float2(pixel) + 0.5f) / float2(size);
    float3 cameraRelativePosition = ReconstructCameraRelativePosition(screenUv, depth);
    float3 worldPosition = cameraRelativePosition + camera.cameraOrigin;
    float3 normal = DecodeNormal(GetTexture(normalHandle).Load(int3(pixel, 0)).xyz);
    float3 viewDirection = normalize(-cameraRelativePosition);
    float3 reflectionDirection = normalize(reflect(-viewDirection, normal));

    RayDesc ray;
    float launchBias = max(bias, 0.001f);
    ray.Origin =
        worldPosition +
        normal * launchBias +
        reflectionDirection * launchBias;
    ray.Direction = reflectionDirection;
    ray.TMin = launchBias;
    ray.TMax = maxRayDistance;
    WolfRayHit hit = WolfTraceClosest(sceneTlas, ray, 0xFF);
    if (!hit.hit)
    {
        return;
    }
    float hitDistance = hit.distance;

    // A ray that reaches the sky adds nothing over the environment specular that deferred
    // lighting already applies, so leave the weight at zero.
    float3 hitPosition = ray.Origin + reflectionDirection * hitDistance;
    float3 screenRadiance;
    float screenReuseWeight = ComputeScreenReuseWeight(
        hitPosition,
        roughness,
        hitDistance,
        pixel,
        screenRadiance);
    float3 reflectedRadiance = screenRadiance;
    if (screenReuseWeight < 1.0f)
    {
        // Shading the hit material is the expensive path, so it stays skipped wherever screen
        // reuse is fully trusted. Partially trusted hits pay for both and cross-fade.
        float3 materialRadiance;
        if (!TryShadeHitMaterial(
            hit.instanceIndex,
            hit.primitiveIndex,
            hit.barycentrics,
            hitPosition,
            reflectionDirection,
            materialRadiance))
        {
            // Geometry without a resolvable material still occludes the sky.
            materialRadiance =
                SamplePrefiltered(prefilteredHandle, samplerHandle, reflectionDirection, roughness) *
                IBL_EXPOSURE * 0.25f;
        }
        reflectedRadiance = lerp(materialRadiance, screenRadiance, screenReuseWeight);
    }

    outputTexture[outputPixel] = float4(reflectedRadiance * intensity, saturate(roughnessFade));
}
