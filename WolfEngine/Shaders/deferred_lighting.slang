SamplerState linearSampler : register(s0);

Texture2D gbufferAlbedo : register(t0);
Texture2D gbufferNormal : register(t1);
Texture2D gbufferMaterial : register(t2);

cbuffer CameraParams : register(b0)
{
    row_major float4x4 viewProjection;
    float3 cameraPosition;
    float padding;
    float4 extra0;
    float4 extra1;
}

struct VSOutput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

VSOutput vertexShader(uint vertexId : SV_VertexID)
{
    float2 positions[4] = {
        float2(-1.0f, -1.0f),
        float2(-1.0f,  1.0f),
        float2( 1.0f, -1.0f),
        float2( 1.0f,  1.0f)
    };

    float2 uvs[4] = {
        float2(0.0f, 1.0f),
        float2(0.0f, 0.0f),
        float2(1.0f, 1.0f),
        float2(1.0f, 0.0f)
    };

    VSOutput output;
    output.position = float4(positions[vertexId], 0.0f, 1.0f);
    output.uv = uvs[vertexId];
    return output;
}

float3 DecodeNormal(float3 encoded)
{
    return normalize(encoded * 2.0f - 1.0f);
}

float4 fragmentShader(VSOutput input) : SV_Target
{
    float3 albedo = gbufferAlbedo.Sample(linearSampler, input.uv).rgb;
    float3 normal = DecodeNormal(gbufferNormal.Sample(linearSampler, input.uv).xyz);
    float metallic = gbufferMaterial.Sample(linearSampler, input.uv).r;

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
    return float4(color, 1.0f);
}
