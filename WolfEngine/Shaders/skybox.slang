import "common_bindless.slang";

cbuffer SkyboxParams : register(b0)
{
    row_major float4x4 viewProjection;
};

cbuffer SkyboxBindless : register(b4)
{
    uint environmentHandle;
    uint samplerHandle;
};

struct VSInput
{
    float3 position : POSITION;
};

struct VSOutput
{
    float4 position : SV_Position;
    float3 direction : TEXCOORD0;
};

VSOutput vertexShader(VSInput input)
{
    VSOutput output;
    float3 dir = normalize(input.position);
    float4 clipPos = mul(float4(dir, 1.0f), viewProjection);
    output.position = clipPos;
    output.direction = dir;
    return output;
}

float4 fragmentShader(VSOutput input) : SV_Target0
{
    float3 dir = normalize(input.direction);
    float2 envUV = float2(atan2(dir.z, dir.x) / (2.0f * 3.14159265f) + 0.5f, dir.y * -0.5f + 0.5f);
    Texture2D<float4> environmentTex = GetTexture(environmentHandle);
    SamplerState linearClamp = GetSampler(samplerHandle);
    float3 color = environmentTex.Sample(linearClamp, envUV).rgb;
    return float4(color, 1.0f);
}
