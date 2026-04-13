import "constants.slang";
import "common_bindless.slang";

static const uint MAX_DIRECTIONAL_LIGHTS = 4;
static const float IBL_EXPOSURE = 0.75f;
static const uint CLUSTER_TILE_SIZE = 64;
static const uint CLUSTER_Z_SLICE_COUNT = 24;
static const uint CLUSTER_MAX_LIGHTS_PER_CLUSTER = 64;

struct DirectionalLightData
{
    float4 colorIntensity;
    float4 directionAndType;
};

struct PointLightData
{
    float4 colorIntensity;
    float4 worldPositionRange;
    float4 viewPositionRange;
};

struct ClusterHeader
{
    uint offset;
    uint count;
};

struct ClusterAabb
{
    float4 minPoint;
    float4 maxPoint;
};

struct SurfaceLightingInput
{
    float3 worldPos;
    float3 normal;
    float3 albedo;
    float metallic;
    float roughness;
    float occlusion;
    float3 emissive;
};

float3 DecodeNormal(float3 encoded)
{
    return normalize(encoded * 2.0f - 1.0f);
}

float2 DirToEnvUV(float3 dir)
{
    dir = normalize(dir);
    float u = atan2(dir.z, dir.x) / (2.0f * PI) + 0.5f;
    float v = acos(clamp(dir.y, -1.0f, 1.0f)) / PI;
    return float2(1.0f - u, v);
}

float3 SampleEnvironment(uint environmentHandle, uint samplerHandle, float3 dir)
{
    float2 envUV = DirToEnvUV(dir);
    Texture2D<float4> environmentTex = GetTexture(environmentHandle);
    SamplerState linearSampler = GetSampler(samplerHandle);
    return environmentTex.SampleLevel(linearSampler, envUV, 0.0f).rgb;
}

float3 SamplePrefiltered(uint prefilteredHandle, uint samplerHandle, float3 dir, float roughness)
{
    const float sliceCount = 6.0f;
    float2 uv = DirToEnvUV(dir);
    float slice = saturate(roughness) * (sliceCount - 1.0f);
    float slice0 = floor(slice);
    float slice1 = min(slice0 + 1.0f, sliceCount - 1.0f);
    float t = slice - slice0;
    float sliceHeight = 1.0f / sliceCount;
    float v0 = (uv.y + slice0) * sliceHeight;
    float v1 = (uv.y + slice1) * sliceHeight;
    Texture2D<float4> prefilteredTex = GetTexture(prefilteredHandle);
    SamplerState linearSampler = GetSampler(samplerHandle);
    float3 p0 = prefilteredTex.SampleLevel(linearSampler, float2(uv.x, v0), 0.0f).rgb;
    float3 p1 = prefilteredTex.SampleLevel(linearSampler, float2(uv.x, v1), 0.0f).rgb;
    return lerp(p0, p1, t);
}

float3 FresnelSchlick(float cosTheta, float3 F0)
{
    float f = pow(1.0f - cosTheta, 5.0f);
    return F0 + (1.0f - F0) * f;
}

float DistributionGGX(float NdotH, float alpha)
{
    float a2 = alpha * alpha;
    float denom = (NdotH * NdotH) * (a2 - 1.0f) + 1.0f;
    return a2 / max(PI * denom * denom, 1e-5f);
}

float GeometrySchlickGGX(float NdotV, float alpha)
{
    float k = (alpha + 1.0f);
    k = (k * k) / 8.0f;
    return NdotV / max(NdotV * (1.0f - k) + k, 1e-5f);
}

float GeometrySmith(float NdotV, float NdotL, float alpha)
{
    float ggxV = GeometrySchlickGGX(NdotV, alpha);
    float ggxL = GeometrySchlickGGX(NdotL, alpha);
    return ggxV * ggxL;
}

float ShadowCompare(Texture2D<float4> shadowMap, int2 texel, int2 shadowMapSize, float receiverDepth)
{
    if (texel.x < 0 || texel.y < 0 || texel.x >= shadowMapSize.x || texel.y >= shadowMapSize.y)
    {
        return 1.0f;
    }

    float depth = shadowMap.Load(int3(texel, 0)).r;
    return receiverDepth <= depth ? 1.0f : 0.0f;
}

float SampleShadowBilinearPCF(Texture2D<float4> shadowMap, float2 sampleUV, int2 shadowMapSize, float receiverDepth)
{
    float2 texelPos = sampleUV * float2(shadowMapSize) - 0.5f;
    int2 texel00 = int2(floor(texelPos));
    float2 fracPos = saturate(texelPos - float2(texel00));

    float s00 = ShadowCompare(shadowMap, texel00, shadowMapSize, receiverDepth);
    float s10 = ShadowCompare(shadowMap, texel00 + int2(1, 0), shadowMapSize, receiverDepth);
    float s01 = ShadowCompare(shadowMap, texel00 + int2(0, 1), shadowMapSize, receiverDepth);
    float s11 = ShadowCompare(shadowMap, texel00 + int2(1, 1), shadowMapSize, receiverDepth);

    float sx0 = lerp(s00, s10, fracPos.x);
    float sx1 = lerp(s01, s11, fracPos.x);
    return lerp(sx0, sx1, fracPos.y);
}

float ComputeDirectionalShadow(
    float3 worldPos,
    float4x4 shadowViewProjection,
    uint shadowMapHandle,
    uint shadowSamplerHandle,
    float2 shadowTexelSize,
    float depthBias)
{
    float4 shadowPosH = mul(float4(worldPos, 1.0f), shadowViewProjection);
    if (shadowPosH.w <= 0.0f)
    {
        return 1.0f;
    }

    float3 shadowPos = shadowPosH.xyz / shadowPosH.w;
    float2 uv = float2(shadowPos.x * 0.5f + 0.5f, 1.0f - (shadowPos.y * 0.5f + 0.5f));
    if (uv.x <= 0.0f || uv.x >= 1.0f || uv.y <= 0.0f || uv.y >= 1.0f)
    {
        return 1.0f;
    }

    if (shadowPos.z <= 0.0f || shadowPos.z >= 1.0f)
    {
        return 1.0f;
    }

    Texture2D<float4> shadowMap = GetTexture(shadowMapHandle);
    float shadowSamplerCompat = (shadowSamplerHandle == 0u) ? 0.0f : 0.0f;
    uint shadowWidth, shadowHeight;
    shadowMap.GetDimensions(shadowWidth, shadowHeight);
    int2 shadowMapSizeInt = int2(max(int(shadowWidth), 1), max(int(shadowHeight), 1));

    float2 shadowMapSize = 1.0f / max(shadowTexelSize, 1e-6f.xx);
    float2 texelPos = uv * shadowMapSize + 0.5f;
    float2 baseTexel = floor(texelPos);
    float2 s = texelPos - baseTexel;

    float uw0 = 4.0f - 3.0f * s.x;
    float uw1 = 7.0f;
    float uw2 = 1.0f + 3.0f * s.x;
    float u0 = (3.0f - 2.0f * s.x) / uw0 - 2.0f;
    float u1 = (3.0f + s.x) / uw1;
    float u2 = s.x / uw2 + 2.0f;

    float vw0 = 4.0f - 3.0f * s.y;
    float vw1 = 7.0f;
    float vw2 = 1.0f + 3.0f * s.y;
    float v0 = (3.0f - 2.0f * s.y) / vw0 - 2.0f;
    float v1 = (3.0f + s.y) / vw1;
    float v2 = s.y / vw2 + 2.0f;

    float receiverDepth = shadowPos.z - depthBias + shadowSamplerCompat;
    float visibility = 0.0f;

    float2 sampleUV00 = (baseTexel + float2(u0, v0) - 0.5f) * shadowTexelSize;
    float2 sampleUV10 = (baseTexel + float2(u1, v0) - 0.5f) * shadowTexelSize;
    float2 sampleUV20 = (baseTexel + float2(u2, v0) - 0.5f) * shadowTexelSize;
    float2 sampleUV01 = (baseTexel + float2(u0, v1) - 0.5f) * shadowTexelSize;
    float2 sampleUV11 = (baseTexel + float2(u1, v1) - 0.5f) * shadowTexelSize;
    float2 sampleUV21 = (baseTexel + float2(u2, v1) - 0.5f) * shadowTexelSize;
    float2 sampleUV02 = (baseTexel + float2(u0, v2) - 0.5f) * shadowTexelSize;
    float2 sampleUV12 = (baseTexel + float2(u1, v2) - 0.5f) * shadowTexelSize;
    float2 sampleUV22 = (baseTexel + float2(u2, v2) - 0.5f) * shadowTexelSize;

    float w00 = uw0 * vw0;
    float w10 = uw1 * vw0;
    float w20 = uw2 * vw0;
    float w01 = uw0 * vw1;
    float w11 = uw1 * vw1;
    float w21 = uw2 * vw1;
    float w02 = uw0 * vw2;
    float w12 = uw1 * vw2;
    float w22 = uw2 * vw2;

    visibility += SampleShadowBilinearPCF(shadowMap, sampleUV00, shadowMapSizeInt, receiverDepth) * w00;
    visibility += SampleShadowBilinearPCF(shadowMap, sampleUV10, shadowMapSizeInt, receiverDepth) * w10;
    visibility += SampleShadowBilinearPCF(shadowMap, sampleUV20, shadowMapSizeInt, receiverDepth) * w20;
    visibility += SampleShadowBilinearPCF(shadowMap, sampleUV01, shadowMapSizeInt, receiverDepth) * w01;
    visibility += SampleShadowBilinearPCF(shadowMap, sampleUV11, shadowMapSizeInt, receiverDepth) * w11;
    visibility += SampleShadowBilinearPCF(shadowMap, sampleUV21, shadowMapSizeInt, receiverDepth) * w21;
    visibility += SampleShadowBilinearPCF(shadowMap, sampleUV02, shadowMapSizeInt, receiverDepth) * w02;
    visibility += SampleShadowBilinearPCF(shadowMap, sampleUV12, shadowMapSizeInt, receiverDepth) * w12;
    visibility += SampleShadowBilinearPCF(shadowMap, sampleUV22, shadowMapSizeInt, receiverDepth) * w22;

    float weightSum = w00 + w10 + w20 + w01 + w11 + w21 + w02 + w12 + w22;
    return weightSum > 0.0f ? visibility / weightSum : 1.0f;
}

float SampleDirectionalShadowCascade(
    float3 worldPos,
    uint cascadeIndex,
    float4x4 shadowViewProjection0,
    float4x4 shadowViewProjection1,
    float4x4 shadowViewProjection2,
    uint shadowMapHandle0,
    uint shadowMapHandle1,
    uint shadowMapHandle2,
    uint shadowSamplerHandle,
    float2 shadowTexelSize,
    float shadowDepthBias)
{
    if (cascadeIndex == 0u)
    {
        return ComputeDirectionalShadow(worldPos, shadowViewProjection0, shadowMapHandle0, shadowSamplerHandle, shadowTexelSize, shadowDepthBias);
    }

    if (cascadeIndex == 1u)
    {
        return ComputeDirectionalShadow(worldPos, shadowViewProjection1, shadowMapHandle1, shadowSamplerHandle, shadowTexelSize, shadowDepthBias);
    }

    return ComputeDirectionalShadow(worldPos, shadowViewProjection2, shadowMapHandle2, shadowSamplerHandle, shadowTexelSize, shadowDepthBias);
}

float ComputeCascadedDirectionalShadow(
    float3 worldPos,
    float receiverDistance,
    float4x4 shadowViewProjection0,
    float4x4 shadowViewProjection1,
    float4x4 shadowViewProjection2,
    uint shadowMapHandle0,
    uint shadowMapHandle1,
    uint shadowMapHandle2,
    uint shadowSamplerHandle,
    float3 shadowSplits,
    float shadowCascadeBlendDistance,
    float2 shadowTexelSize,
    float shadowDepthBias)
{
    uint cascadeIndex = 2u;
    if (receiverDistance <= shadowSplits.x)
    {
        cascadeIndex = 0u;
    }
    else if (receiverDistance <= shadowSplits.y)
    {
        cascadeIndex = 1u;
    }

    float primary = SampleDirectionalShadowCascade(
        worldPos,
        cascadeIndex,
        shadowViewProjection0,
        shadowViewProjection1,
        shadowViewProjection2,
        shadowMapHandle0,
        shadowMapHandle1,
        shadowMapHandle2,
        shadowSamplerHandle,
        shadowTexelSize,
        shadowDepthBias);

    if (shadowCascadeBlendDistance <= 0.0f)
    {
        return primary;
    }

    if (cascadeIndex < 2u)
    {
        float boundary = cascadeIndex == 0u ? shadowSplits.x : shadowSplits.y;
        if (abs(receiverDistance - boundary) <= shadowCascadeBlendDistance)
        {
            float nextShadow = SampleDirectionalShadowCascade(
                worldPos,
                cascadeIndex + 1u,
                shadowViewProjection0,
                shadowViewProjection1,
                shadowViewProjection2,
                shadowMapHandle0,
                shadowMapHandle1,
                shadowMapHandle2,
                shadowSamplerHandle,
                shadowTexelSize,
                shadowDepthBias);
            float blendRange = max(shadowCascadeBlendDistance * 2.0f, 1e-6f);
            float t = saturate((receiverDistance - (boundary - shadowCascadeBlendDistance)) / blendRange);
            return lerp(primary, nextShadow, t);
        }
    }

    return primary;
}

float3 AccumulateLight(
    SurfaceLightingInput surface,
    float3 viewDir,
    float3 lightColor,
    float3 lightDirection,
    float attenuation,
    float shadowFactor)
{
    float3 normal = normalize(surface.normal);
    float3 H = normalize(lightDirection + viewDir);
    float NdotL = max(dot(normal, lightDirection), 0.0f);
    float NdotV = max(dot(normal, viewDir), 0.0f);
    float NdotH = max(dot(normal, H), 0.0f);
    float VdotH = max(dot(viewDir, H), 0.0f);

    float alpha = max(surface.roughness * surface.roughness, 0.04f);
    float3 F0 = lerp(float3(0.04f, 0.04f, 0.04f), surface.albedo, surface.metallic);
    float3 F = FresnelSchlick(VdotH, F0);
    float D = DistributionGGX(NdotH, alpha);
    float G = GeometrySmith(NdotV, NdotL, alpha);

    float3 specular = (D * G * F) / max(4.0f * NdotV * NdotL, 1e-5f);
    float3 kS = F;
    float3 kD = (1.0f - kS) * (1.0f - surface.metallic);
    float3 diffuse = kD * surface.albedo / PI;
    return (diffuse + specular) * lightColor * attenuation * NdotL * shadowFactor;
}

float3 EvaluateDirectionalLights(
    SurfaceLightingInput surface,
    float3 viewDir,
    uint directionalLightCount,
    DirectionalLightData directionalLights[MAX_DIRECTIONAL_LIGHTS],
    float4x4 shadowViewProjection0,
    float4x4 shadowViewProjection1,
    float4x4 shadowViewProjection2,
    uint shadowMapHandle0,
    uint shadowMapHandle1,
    uint shadowMapHandle2,
    uint shadowSamplerHandle,
    float3 shadowSplits,
    float shadowCascadeBlendDistance,
    uint shadowLightIndex,
    float2 shadowTexelSize,
    float shadowDepthBias,
    float shadowStrength,
    uint shadowsEnabled,
    float shadowMaxDistance)
{
    float3 color = 0.0f.xxx;
    [unroll]
    for (uint i = 0; i < directionalLightCount && i < MAX_DIRECTIONAL_LIGHTS; ++i)
    {
        DirectionalLightData light = directionalLights[i];
        float3 L = -normalize(light.directionAndType.xyz);
        float3 lightColor = light.colorIntensity.rgb * light.colorIntensity.a;
        float shadow = 1.0f;
        if (shadowsEnabled != 0u && i == shadowLightIndex)
        {
            float maxShadowDistanceSq = shadowMaxDistance * shadowMaxDistance;
            float receiverDistanceSq = dot(surface.worldPos, surface.worldPos);
            if (shadowMaxDistance <= 0.0f || receiverDistanceSq <= maxShadowDistanceSq)
            {
                float receiverDistance = sqrt(receiverDistanceSq);
                float pcf = ComputeCascadedDirectionalShadow(
                    surface.worldPos,
                    receiverDistance,
                    shadowViewProjection0,
                    shadowViewProjection1,
                    shadowViewProjection2,
                    shadowMapHandle0,
                    shadowMapHandle1,
                    shadowMapHandle2,
                    shadowSamplerHandle,
                    shadowSplits,
                    shadowCascadeBlendDistance,
                    shadowTexelSize,
                    shadowDepthBias);
                shadow = lerp(1.0f, pcf, saturate(shadowStrength));
            }
        }

        color += AccumulateLight(surface, viewDir, lightColor, L, 1.0f, shadow);
    }

    return color;
}

float3 EvaluatePointLight(
    SurfaceLightingInput surface,
    float3 viewDir,
    PointLightData light)
{
    float3 toLight = light.worldPositionRange.xyz - surface.worldPos;
    float distSq = dot(toLight, toLight);
    float dist = sqrt(distSq);
    float range = max(light.worldPositionRange.w, 0.001f);
    float3 L = dist > 0.0f ? toLight / dist : float3(0.0f, 1.0f, 0.0f);
    float falloff = saturate(1.0f - dist / range);
    float attenuation = falloff * falloff;
    float3 lightColor = light.colorIntensity.rgb * light.colorIntensity.a;
    return AccumulateLight(surface, viewDir, lightColor, L, attenuation, 1.0f);
}

uint ComputeClusterZSlice(float viewDepth, float nearPlane, float farPlane)
{
    float safeDepth = max(viewDepth, nearPlane);
    float logDepth = log2(max(safeDepth / max(nearPlane, 1e-5f), 1.0f));
    float logRange = max(log2(max(farPlane / max(nearPlane, 1e-5f), 1.0f)), 1e-5f);
    float slice = floor((logDepth / logRange) * float(CLUSTER_Z_SLICE_COUNT));
    return min((uint)max(slice, 0.0f), CLUSTER_Z_SLICE_COUNT - 1u);
}

uint ComputeClusterIndex(uint2 pixel, uint2 framebufferSize, float viewDepth, float nearPlane, float farPlane)
{
    uint clusterCountX = max((framebufferSize.x + CLUSTER_TILE_SIZE - 1u) / CLUSTER_TILE_SIZE, 1u);
    uint clusterCountY = max((framebufferSize.y + CLUSTER_TILE_SIZE - 1u) / CLUSTER_TILE_SIZE, 1u);
    uint clusterX = min(pixel.x / CLUSTER_TILE_SIZE, clusterCountX - 1u);
    uint clusterY = min(pixel.y / CLUSTER_TILE_SIZE, clusterCountY - 1u);
    uint clusterZ = ComputeClusterZSlice(viewDepth, nearPlane, farPlane);
    return clusterX + (clusterY * clusterCountX) + (clusterZ * clusterCountX * clusterCountY);
}

float3 EvaluateImageBasedLighting(
    SurfaceLightingInput surface,
    float3 viewDir,
    uint irradianceHandle,
    uint prefilteredHandle,
    uint brdfLutHandle,
    uint samplerHandle,
    float iblExposure)
{
    float3 normal = normalize(surface.normal);
    float3 albedo = surface.albedo;
    float metallic = surface.metallic;
    float roughness = surface.roughness;
    float occlusion = surface.occlusion;

    SamplerState linearSampler = GetSampler(samplerHandle);
    Texture2D<float4> irradianceTex = GetTexture(irradianceHandle);
    float3 envIrradiance = irradianceTex.SampleLevel(linearSampler, DirToEnvUV(normal), 0.0f).rgb * iblExposure;
    float3 F0 = lerp(float3(0.04f, 0.04f, 0.04f), albedo, metallic);
    float3 kD = (1.0f - metallic) * (1.0f - F0);
    float3 color = envIrradiance * kD * albedo / PI * occlusion;

    float3 reflectionDir = reflect(-viewDir, normal);
    float3 envColor = SamplePrefiltered(prefilteredHandle, samplerHandle, reflectionDir, roughness) * iblExposure;

    float cosTheta = saturate(dot(normal, viewDir));
    float3 fresnel = FresnelSchlick(cosTheta, F0);
    Texture2D<float4> brdfLutTex = GetTexture(brdfLutHandle);
    float2 brdf = brdfLutTex.SampleLevel(linearSampler, float2(cosTheta, roughness), 0.0f).rg;
    float3 specular = envColor * (fresnel * brdf.x + brdf.y);
    color += specular * 0.5f * occlusion;

    return color;
}
