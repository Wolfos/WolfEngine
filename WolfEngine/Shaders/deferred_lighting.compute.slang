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

    float3 lightDirection = normalize(float3(-0.5f, -1.0f, -0.2f));
    float3 lightColor = float3(1.0f, 1.0f, 1.0f);
    float ambientStrength = 0.5f;

    float diffuse = max(dot(normal, -lightDirection), 0.0f);
    float3 ambient = ambientStrength * albedo;
    float3 diffuseComponent = diffuse * albedo * lightColor;

    float roughness = lerp(0.2f, 0.8f, metallic);
    float3 halfVector = normalize(-lightDirection + viewDir);
    float specular = pow(max(dot(normal, halfVector), 0.0f), lerp(32.0f, 4.0f, roughness));
    float3 specularComponent = specular * lightColor * 0.5f;

    float3 color = ambient + diffuseComponent + specularComponent;
    lightingTarget[dispatchThreadId.xy] = float4(color, 1.0f);
}
