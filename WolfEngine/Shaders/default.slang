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
    return float4(182.0f / 255.0f, 240.0f / 255.0f, 228.0f / 255.0f, 1.0f);
}
