cbuffer MaterialParams : register(b0)
{
    float4 baseColor;
};

Texture2D albedoTex : register(t0);
Texture2D metallicRoughnessTex : register(t1);
Texture2D normalTex : register(t2);
Texture2D occlusionTex : register(t3);
Texture2D emissiveTex : register(t4);
SamplerState linearSampler : register(s0);

cbuffer DrawParams : register(b1)
{
    row_major float4x4 model;
};

cbuffer CameraParams : register(b2)
{
    row_major float4x4 viewProjection;
    float3 cameraPosition;
    float padding;
};

struct VSInput
{
    float4 position : POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
    float4 tangent : TANGENT;
};

struct VSOutput
{
    float4 position : SV_Position;
    float3 normal : TEXCOORD0;
    float2 uv : TEXCOORD1;
    float4 tangent : TEXCOORD2;
};

VSOutput vertexShader(VSInput input)
{
    VSOutput output;
    float4 worldPosition = mul(input.position, model);
    float4 worldNormal = mul(float4(input.normal, 0.0f), model);
    float3 worldTangent = mul(float4(input.tangent.xyz, 0.0f), model).xyz;

    output.position = mul(worldPosition, viewProjection);
    output.normal = normalize(worldNormal.xyz);
    output.uv = input.uv;
    output.tangent = float4(normalize(worldTangent), input.tangent.w);
    return output;
}

struct PSOutput
{
    float4 albedo : SV_Target0;
    float4 normal : SV_Target1;
    float4 material : SV_Target2;
    float4 emissive : SV_Target3;
};

PSOutput fragmentShader(VSOutput input)
{
    PSOutput output;

    
    float3 normal = normalize(input.normal);


    float3 tangentNormal = normalize(normalTex.Sample(linearSampler, input.uv).rgb * 2.0f - 1.0f);

    float3 N = normal;
    float3 T = normalize(input.tangent.xyz - dot(input.tangent.xyz, N) * N);
    float3 B = normalize(cross(N, T)) * input.tangent.w;

    float3x3 TBN = float3x3(T, B, N);

    float3 worldNormalMap = mul(tangentNormal, TBN);
    normal += worldNormalMap;

    float4 albedoSample = albedoTex.Sample(linearSampler, input.uv);
    float4 mrSample = metallicRoughnessTex.Sample(linearSampler, input.uv);
    float4 emissiveSample = emissiveTex.Sample(linearSampler, input.uv);


    float metallic = mrSample.g;
    float roughness = mrSample.b;
    float4 materialParams = float4(metallic, roughness, 0.0f, 1.0f);
    float4 albedo = baseColor * albedoSample;
    output.albedo = albedo;
    output.normal = float4(normal * 0.5f + 0.5f, 1.0f);
    output.material = materialParams;
    output.emissive = emissiveSample;
    return output;
}
