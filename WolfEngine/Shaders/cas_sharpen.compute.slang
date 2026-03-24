import "common_bindless.slang";

// Adapted from AMD FidelityFX CAS (Contrast Adaptive Sharpening), sharpen-only path.
// The original vendor sources are bundled under Shaders/ffx-cas with the upstream license text.

cbuffer BindlessHandles : register(b0)
{
    uint inputHandle;
    uint outputHandle;
};

cbuffer CasSettings : register(b1)
{
    uint renderSizeX;
    uint renderSizeY;
    uint sharpenEnabled;
    uint _padding0;
    uint casConst0X;
    uint casConst0Y;
    uint casConst0Z;
    uint casConst0W;
    uint casConst1X;
    uint casConst1Y;
    uint casConst1Z;
    uint casConst1W;
};

float3 LinearToSrgb(float3 color)
{
    float3 cutoff = step(color, 0.0031308f.xxx);
    float3 lower = color * 12.92f;
    float3 upper = 1.055f * pow(abs(color), 1.0f / 2.4f) - 0.055f;
    return lerp(upper, lower, cutoff);
}

int2 ClampPixel(int2 pixel, uint2 size)
{
    return clamp(pixel, int2(0, 0), int2(int(size.x) - 1, int(size.y) - 1));
}

float3 CasLoad(Texture2D<float4> texture, int2 pixel, uint2 size)
{
    return texture.Load(int3(ClampPixel(pixel, size), 0)).rgb;
}

float3 CasFilterNoScaling(Texture2D<float4> texture, int2 pixel, uint2 size, uint4 casConst1)
{
    float3 a = CasLoad(texture, pixel + int2(-1, -1), size);
    float3 b = CasLoad(texture, pixel + int2(0, -1), size);
    float3 c = CasLoad(texture, pixel + int2(1, -1), size);
    float3 d = CasLoad(texture, pixel + int2(-1, 0), size);
    float3 e = CasLoad(texture, pixel, size);
    float3 f = CasLoad(texture, pixel + int2(1, 0), size);
    float3 g = CasLoad(texture, pixel + int2(-1, 1), size);
    float3 h = CasLoad(texture, pixel + int2(0, 1), size);
    float3 i = CasLoad(texture, pixel + int2(1, 1), size);

    float3 mn = min(min(min(d, e), f), min(b, h));
    float3 mx = max(max(max(d, e), f), max(b, h));
    float3 amp = saturate(min(mn, 1.0f.xxx - mx) / max(mx, 1e-4f.xxx));
    amp = sqrt(amp);

    float peak = asfloat(casConst1.x);
    float w = amp.g * peak;
    float rcpWeight = rcp(1.0f + 4.0f * w);

    return saturate((b + d + f + h) * w * rcpWeight + e * rcpWeight);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= renderSizeX || dispatchThreadId.y >= renderSizeY)
    {
        return;
    }

    Texture2D<float4> inputTexture = GetTexture(inputHandle);
    RWTexture2D<float4> outputTexture = GetRWTexture(outputHandle);

    int2 pixel = int2(dispatchThreadId.xy);
    uint2 renderSize = uint2(renderSizeX, renderSizeY);
    uint4 casConst1 = uint4(casConst1X, casConst1Y, casConst1Z, casConst1W);

    float3 linearColor = sharpenEnabled != 0u
        ? CasFilterNoScaling(inputTexture, pixel, renderSize, casConst1)
        : inputTexture.Load(int3(pixel, 0)).rgb;
    float3 encoded = saturate(LinearToSrgb(saturate(linearColor)));
    outputTexture[pixel] = float4(encoded, 1.0f);
}
