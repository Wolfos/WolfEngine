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
    float3 worldPosition : TEXCOORD1;
};

VSOutput vertexShader(VSInput input)
{
    VSOutput output;
    float4 worldPosition = mul(input.position, model);
    float4 worldNormal = mul(float4(input.normal, 0.0f), model);

    output.position = mul(worldPosition, viewProjection);
    output.normal = normalize(worldNormal.xyz);
    output.worldPosition = worldPosition.xyz;
    return output;
}

float4 fragmentShader(VSOutput input) : SV_Target
{
    const float3 lightDirection = normalize(float3(-0.5f, -1.0f, -0.2f));
    const float3 lightColor = float3(1.0f, 1.0f, 1.0f);
    const float ambientStrength = 0.1f;
    const float specularStrength = 0.4f;
    const float shininess = 32.0f;

    float3 normal = normalize(input.normal);
    float3 viewDirection = normalize(cameraPosition - input.worldPosition);
    float3 lightDir = normalize(-lightDirection);

    float diffuse = max(dot(normal, lightDir), 0.0f);

    float3 halfVector = normalize(lightDir + viewDirection);
    float specular = pow(max(dot(normal, halfVector), 0.0f), shininess);

    float3 ambientComponent = ambientStrength * baseColor.rgb;
    float3 diffuseComponent = diffuse * baseColor.rgb * lightColor;
    float3 specularComponent = specularStrength * specular * lightColor;

    float3 finalColor = ambientComponent + diffuseComponent + specularComponent;
    return float4(finalColor, baseColor.a);
}
