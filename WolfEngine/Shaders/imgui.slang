import "common_bindless.slang";

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

cbuffer ImGuiBindless : register(b1)
{
    uint textureHandle;
    uint samplerHandle;
};

PixelInput vertexShader(VertexInput input)
{
    PixelInput output;
    output.Position = mul(float4(input.Position, 0.0f, 1.0f), ProjectionMatrix);
    output.UV = input.UV;
    output.Color = input.Color;
    return output;
}

float4 fragmentShader(PixelInput input) : SV_TARGET
{
    Texture2D<float4> texture0 = GetTexture(textureHandle);
    SamplerState sampler0 = GetSampler(samplerHandle);
    return input.Color * texture0.Sample(sampler0, input.UV);
}
