import "common.slang";

SamplerState linearSampler : register(s0);

Texture2D gbufferAlbedo : register(t0);
Texture2D gbufferNormal : register(t1);
Texture2D gbufferMaterial : register(t2);
Texture2D gbufferEmissive : register(t3);
Texture2D<float> gbufferDepth : register(t4);
Texture2D environmentTex : register(t5);
Texture2D irradianceTex : register(t6);
Texture2D prefilteredTex : register(t7);
Texture2D brdfLutTex : register(t8);
RWTexture2D<float4> lightingTarget : register(u0);

cbuffer CameraParams : register(b0)
{
    CameraParams camera;
};

static const uint MAX_LIGHTS = 3;

struct LightData
{
    float4 colorIntensity;   // rgb = color, a = intensity
    float4 directionType;    // xyz = direction (normalized), w = type (0 = directional, 1 = point)
    float4 positionRange;    // xyz = position, w = range (point only)
};

cbuffer LightingParams : register(b1)
{
    uint lightCount;
    float3 _lightingPadding;
    LightData lights[MAX_LIGHTS];
};

float3 DecodeNormal(float3 encoded)
{
    return normalize(encoded * 2.0f - 1.0f);
}

float2 DirToEnvUV(float3 dir)
{
    dir = normalize(dir);
    return float2(atan2(dir.z, dir.x) / (2.0f * 3.14159265f) + 0.5f, -dir.y * 0.5f + 0.5f);
}

float3 SampleEnvironment(float3 dir)
{
    float2 envUV = DirToEnvUV(dir);
    return environmentTex.SampleLevel(linearSampler, envUV, 0.0f).rgb;
}

float3 SamplePrefiltered(float3 dir, float roughness)
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
    float3 p0 = prefilteredTex.SampleLevel(linearSampler, float2(uv.x, v0), 0.0f).rgb;
    float3 p1 = prefilteredTex.SampleLevel(linearSampler, float2(uv.x, v1), 0.0f).rgb;
    return lerp(p0, p1, t);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint width, height;
    lightingTarget.GetDimensions(width, height);
    if (dispatchThreadId.x >= width || dispatchThreadId.y >= height)
    {
        return;
    }

    float2 uv = (float2(dispatchThreadId.xy) + 0.5f) / float2(width, height);

    float3 albedo = gbufferAlbedo.SampleLevel(linearSampler, uv, 0.0f).rgb;
    float3 normal = DecodeNormal(gbufferNormal.SampleLevel(linearSampler, uv, 0.0f).xyz);
    float4 material = gbufferMaterial.SampleLevel(linearSampler, uv, 0.0f);
    float3 emissive = gbufferEmissive.SampleLevel(linearSampler, uv, 0.0f).rgb;

    float metallic = material.r;
    float roughness = material.g;
    float depth = gbufferDepth.Load(int3(dispatchThreadId.xy, 0));
    if (depth >= 0.9999f) // no geometry, keep background untouched
    {
        lightingTarget[dispatchThreadId.xy] = float4(albedo, 0);
        return;
    }

    float3 viewPos = ReconstructViewPosition(dispatchThreadId.xy, uint2(width, height), depth, camera.invProjection);
    float3 viewDir = ComputeViewDir(viewPos);

    
    float3 envIrradiance = irradianceTex.SampleLevel(linearSampler, DirToEnvUV(normal), 0.0f).rgb;
    float3 color = albedo * (1.0f - metallic);

    [unroll]
    for (uint i = 0; i < lightCount && i < MAX_LIGHTS; ++i)
    {
        LightData light = lights[i];
        float intensity = light.colorIntensity.a;
        float3 lightColor = light.colorIntensity.rgb * intensity;

        float type = light.directionType.w;
        float3 L = -light.directionType.xyz; // assume stored as light-to-surface direction
        float attenuation = 1.0f;

        if (type >= 0.5f) // point
        {
            float3 lightPos = light.positionRange.xyz;
            float range = max(light.positionRange.w, 0.001f);
            float3 toLight = lightPos - viewPos;
            float distSq = dot(toLight, toLight);
            float dist = sqrt(distSq);
            L = dist > 0.0f ? toLight / dist : float3(0, 1, 0);
            float falloff = saturate(1.0f - dist / range);
            attenuation = falloff * falloff;
        }

        float NdotL = max(dot(normal, L), 0.0f);
        float3 diffuse = NdotL * albedo * lightColor * attenuation;

        float roughness = lerp(0.2f, 0.8f, metallic);
        float3 halfVector = normalize(L + viewDir);
        float specular = pow(max(dot(normal, halfVector), 0.0f), lerp(32.0f, 4.0f, roughness));
        float3 specularComponent = specular * lightColor * 0.5f * attenuation;

        color += diffuse + specularComponent + emissive;
    }

    color += envIrradiance;

    // Image-based reflection (prefiltered)
    float3 reflectionDir = reflect(-viewDir, normal);
    float3 envColor = SamplePrefiltered(reflectionDir, roughness);

    float3 F0 = lerp(float3(0.04f, 0.04f, 0.04f), albedo, metallic);
    float cosTheta = saturate(dot(normal, -viewDir));
    float3 fresnel = F0 + (1.0f - F0) * pow(1.0f - cosTheta, 5.0f);
    float2 brdf = brdfLutTex.SampleLevel(linearSampler, float2(cosTheta, roughness), 0.0f).rg;
    float3 specular = envColor * (fresnel * brdf.x + brdf.y);
    color += specular;

    lightingTarget[dispatchThreadId.xy] = float4(color, 1.0f);
}
