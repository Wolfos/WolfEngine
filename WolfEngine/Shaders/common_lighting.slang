import "constants.slang";
import "common_bindless.slang";

static const uint MAX_LIGHTS = 3;
static const float IBL_EXPOSURE = 1.0f;

struct LightData
{
    float4 colorIntensity;   // rgb = color, a = intensity
    float4 directionType;    // xyz = direction (normalized), w = type (0 = directional, 1 = point)
    float4 positionRange;    // xyz = position, w = range (point only)
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

float3 EvaluateDirectLighting(SurfaceLightingInput surface, float3 viewDir, uint lightCount, LightData lights[MAX_LIGHTS])
{
    float3 color = float3(0.0f, 0.0f, 0.0f);
    float3 normal = normalize(surface.normal);
    float3 albedo = surface.albedo;
    float metallic = surface.metallic;
    float roughness = surface.roughness;

    [unroll]
    for (uint i = 0; i < lightCount && i < MAX_LIGHTS; ++i)
    {
        LightData light = lights[i];
        float intensity = light.colorIntensity.a;
        float3 lightColor = light.colorIntensity.rgb * intensity;

        float type = light.directionType.w;
        float3 L = -light.directionType.xyz;
        float attenuation = 1.0f;

        if (type >= 0.5f)
        {
            float3 lightPos = light.positionRange.xyz;
            float range = max(light.positionRange.w, 0.001f);
            float3 toLight = lightPos - surface.worldPos;
            float distSq = dot(toLight, toLight);
            float dist = sqrt(distSq);
            L = dist > 0.0f ? toLight / dist : float3(0, 1, 0);
            float falloff = saturate(1.0f - dist / range);
            attenuation = falloff * falloff;
        }

        float3 H = normalize(L + viewDir);
        float NdotL = max(dot(normal, L), 0.0f);
        float NdotV = max(dot(normal, viewDir), 0.0f);
        float NdotH = max(dot(normal, H), 0.0f);
        float VdotH = max(dot(viewDir, H), 0.0f);

        float alpha = max(roughness * roughness, 0.04f);
        float3 F0 = lerp(float3(0.04f, 0.04f, 0.04f), albedo, metallic);
        float3 F = FresnelSchlick(VdotH, F0);
        float D = DistributionGGX(NdotH, alpha);
        float G = GeometrySmith(NdotV, NdotL, alpha);

        float3 specular = (D * G * F) / max(4.0f * NdotV * NdotL, 1e-5f);
        float3 kS = F;
        float3 kD = (1.0f - kS) * (1.0f - metallic);
        float3 diffuse = kD * albedo / PI;

        float3 radiance = lightColor * attenuation;
        color += (diffuse + specular) * radiance * NdotL;
    }

    return color;
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

float3 EvaluateSurfaceLighting(
    SurfaceLightingInput surface,
    float3 viewDir,
    uint lightCount,
    LightData lights[MAX_LIGHTS],
    uint irradianceHandle,
    uint prefilteredHandle,
    uint brdfLutHandle,
    uint samplerHandle,
    float iblExposure)
{
    float3 color = EvaluateDirectLighting(surface, viewDir, lightCount, lights);
    color += EvaluateImageBasedLighting(
        surface,
        viewDir,
        irradianceHandle,
        prefilteredHandle,
        brdfLutHandle,
        samplerHandle,
        iblExposure);
    color += surface.emissive;
    return color;
}
