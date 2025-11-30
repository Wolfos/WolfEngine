struct VertexInput
{
    float2 Position : POSITION;
    float2 UV : TEXCOORD0;
    float4 Color : COLOR0;
};

struct PixelInput
{
    float4 Position : SV_POSITION;
    float2 UV : TEXCOORD0;
    float4 Color : COLOR0;
};

cbuffer Projection : register(b0)
{
    float4x4 ProjectionMatrix;
}

PixelInput vertexShader(VertexInput input)
{
    PixelInput output;
    output.Position = mul(float4(input.Position, 0.0f, 1.0f), ProjectionMatrix);
    output.UV = input.UV;
    output.Color = input.Color;
    return output;
}

Texture2D Texture0 : register(t0);
SamplerState Sampler0 : register(s0);

float4 fragmentShader(PixelInput input) : SV_TARGET
{
    return input.Color * Texture0.Sample(Sampler0, input.UV);
}
