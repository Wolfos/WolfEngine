#if defined(WOLF_TARGET_METAL)
import "../Common/common_bindless.slang";

cbuffer ImGuiBindless : register(b1)
{
    uint textureHandle;
    uint samplerHandle;
    uint sampleTexture;
    float distanceRange;
};
#else
cbuffer UiDraw : register(b1)
{
    uint sampleTexture;
    float distanceRange;
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
    // Solid geometry can share the atlas batch without being textured.
    if (sampleTexture == 0 || input.UV.x < 0) return input.Color;
#if defined(WOLF_TARGET_METAL)
    Texture2D<float4> texture0 = GetTexture(textureHandle);
    SamplerState sampler0 = GetSampler(samplerHandle);
    float4 sampled = texture0.Sample(sampler0, input.UV);
#else
    Texture2D<float4> texture0 = g_FontTexture;
    float4 sampled = texture0.Sample(g_FontSampler, input.UV);
#endif
    if (sampleTexture == 2)
    {
        uint width, height;
        texture0.GetDimensions(width, height);
        float median = max(min(sampled.r, sampled.g), min(max(sampled.r, sampled.g), sampled.b));
        float2 unitRange = distanceRange / float2(width, height);
        float2 screenTexSize = 1.0 / max(abs(ddx(input.UV)) + abs(ddy(input.UV)), float2(0.000001));
        float screenRange = max(0.5 * dot(unitRange, screenTexSize), 1.0);
        return float4(input.Color.rgb, input.Color.a * saturate((median - 0.5) * screenRange + 0.5));
    }
    // The texture's alpha holds data rather than coverage, so only the vertex alpha blends.
    if (sampleTexture == 3) return float4(input.Color.rgb * sampled.rgb, input.Color.a);
    return input.Color * sampled;
}

float4 solidFragmentShader(PixelInput input) : SV_TARGET
{
#if !defined(WOLF_TARGET_METAL)
    float4 sampled = g_FontTexture.Sample(g_FontSampler, input.UV);
    // Retain the shared UI binding layout while making gameplay geometry effectively untextured.
    return input.Color + sampled * 0.000001f;
#else
    return input.Color;
#endif
}
