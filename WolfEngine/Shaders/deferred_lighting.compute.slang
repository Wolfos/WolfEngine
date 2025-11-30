import "common.slang";

SamplerState linearSampler : register(s0);

Texture2D gbufferAlbedo : register(t0);
Texture2D gbufferNormal : register(t1);
Texture2D gbufferMaterial : register(t2);
Texture2D<float> gbufferDepth : register(t3);
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
    float metallic = gbufferMaterial.SampleLevel(linearSampler, uv, 0.0f).r;
    float depth = gbufferDepth.Load(int3(dispatchThreadId.xy, 0));

    float3 viewPos = ReconstructViewPosition(dispatchThreadId.xy, uint2(width, height), depth, camera.invProjection);
    float3 viewDir = ComputeViewDir(viewPos);

    float3 color = float3(0.05f, 0.05f, 0.05f) * albedo; // ambient

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

        color += diffuse + specularComponent;
    }

    lightingTarget[dispatchThreadId.xy] = float4(color, 1.0f);
}
