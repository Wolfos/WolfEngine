cbuffer MaterialParams : register(b0)
{
    float4 baseColor;
};

cbuffer DrawParams : register(b1)
{
    row_major float4x4 model;
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
    output.position = mul(input.position, model);
    return output;
}

float4 fragmentShader(VSOutput input) : SV_Target
{
    return baseColor;
}
