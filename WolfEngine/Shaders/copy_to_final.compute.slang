import "common_bindless.slang";

cbuffer BindlessHandles : register(b0)
{
    uint inputHandle;
    uint outputHandle;
    uint encodedSceneOutputHandle;
};

cbuffer CopySettings : register(b1)
{
    uint renderSizeX;
    uint renderSizeY;
};

float3 LinearToSrgb(float3 color)
{
    float3 cutoff = step(color, 0.0031308f.xxx);
    float3 lower = color * 12.92f;
    float3 upper = 1.055f * pow(color, 1.0f / 2.4f) - 0.055f;
    return lerp(upper, lower, cutoff);
}

[numthreads(8, 8, 1)]
void CopyToFinalCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= renderSizeX || dispatchThreadId.y >= renderSizeY)
    {
        return;
    }

    Texture2D<float4> inputTexture = GetTexture(inputHandle);
    RWTexture2D<float4> outputTexture = GetRWTexture(outputHandle);
    RWTexture2D<float4> encodedSceneOutputTexture = GetRWTexture(encodedSceneOutputHandle);
    int2 pixel = int2(dispatchThreadId.xy);
    float4 linearDisplayColor = inputTexture.Load(int3(pixel, 0));
    float4 encodedDisplayColor = float4(LinearToSrgb(saturate(linearDisplayColor.rgb)), linearDisplayColor.a);
    outputTexture[pixel] = encodedDisplayColor;
    encodedSceneOutputTexture[pixel] = encodedDisplayColor;
}
