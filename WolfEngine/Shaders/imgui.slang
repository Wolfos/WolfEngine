#if defined(WOLF_TARGET_METAL)
import "common_bindless.slang";

cbuffer ImGuiBindless : register(b1)
{
    uint textureHandle;
    uint samplerHandle;
};
#endif

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

Texture2D<float4> g_FontTexture : register(t0);
SamplerState g_FontSampler : register(s0);

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
#if defined(WOLF_TARGET_METAL)
    Texture2D<float4> texture0 = GetTexture(textureHandle);
    SamplerState sampler0 = GetSampler(samplerHandle);
    return input.Color * texture0.Sample(sampler0, input.UV);
#else
    // ImGui currently renders from a single font texture in the D3D12 backend.
    return input.Color * g_FontTexture.Sample(g_FontSampler, input.UV);
#endif
}
