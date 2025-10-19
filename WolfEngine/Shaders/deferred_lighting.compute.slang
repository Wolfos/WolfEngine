SamplerState linearSampler : register(s0);

Texture2D gbufferAlbedo : register(t0);
Texture2D gbufferNormal : register(t1);
Texture2D gbufferMaterial : register(t2);
RWTexture2D<float4> lightingTarget : register(u0);

cbuffer CameraParams : register(b0)
{
    row_major float4x4 viewProjection;
    float3 cameraPosition;
    float padding;
    float4 extra0;
    float4 extra1;
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

    float3 lightDirection = normalize(float3(-0.5f, -1.0f, -0.2f));
    float3 lightColor = float3(1.0f, 1.0f, 1.0f);
    float ambientStrength = 0.05f;

    float diffuse = max(dot(normal, -lightDirection), 0.0f);
    float3 ambient = ambientStrength * albedo;
    float3 diffuseComponent = diffuse * albedo * lightColor;

    float roughness = lerp(0.2f, 0.8f, metallic);
    float3 viewDir = normalize(cameraPosition);
    float3 halfVector = normalize(-lightDirection + viewDir);
    float specular = pow(max(dot(normal, halfVector), 0.0f), lerp(32.0f, 4.0f, roughness));
    float3 specularComponent = specular * lightColor * 0.5f;

    float3 color = ambient + diffuseComponent + specularComponent;
    lightingTarget[dispatchThreadId.xy] = float4(color, 1.0f);
}
