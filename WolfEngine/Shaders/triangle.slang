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
    return float4(1.0f, 1.0f, 1.0f, 1.0f);
}
