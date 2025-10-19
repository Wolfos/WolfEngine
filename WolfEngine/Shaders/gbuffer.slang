cbuffer MaterialParams : register(b0)
{
    float4 baseColor;
};

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
};

struct VSOutput
{
    float4 position : SV_Position;
    float3 normal : TEXCOORD0;
};

VSOutput vertexShader(VSInput input)
{
    VSOutput output;
    float4 worldPosition = mul(input.position, model);
    float4 worldNormal = mul(float4(input.normal, 0.0f), model);

    output.position = mul(worldPosition, viewProjection);
    output.normal = normalize(worldNormal.xyz);
    return output;
}

struct PSOutput
{
    float4 albedo : SV_Target0;
    float4 normal : SV_Target1;
    float4 material : SV_Target2;
};

PSOutput fragmentShader(VSOutput input)
{
    PSOutput output;
    float3 normal = normalize(input.normal);
    output.albedo = baseColor;
    output.normal = float4(normal * 0.5f + 0.5f, 1.0f);
    output.material = float4(baseColor.a, 0.0f, 0.0f, 1.0f);
    return output;
}
