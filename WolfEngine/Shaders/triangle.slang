cbuffer MaterialParams : register(b0)
{
    float4 baseColor;
};

struct VSInput
{
    float4 position : POSITION;
};

struct VSOutput
{
    float4 position : SV_Position;
};

VSOutput vertexShader(VSInput input)
{
    VSOutput output;
    output.position = input.position;
    return output;
}

float4 fragmentShader(VSOutput input) : SV_Target
{
    return baseColor;
}
