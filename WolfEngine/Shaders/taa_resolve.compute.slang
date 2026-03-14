import "common_bindless.slang";

cbuffer BindlessHandles : register(b0)
{
    uint currentColorHandle;
    uint velocityHandle;
    uint materialHandle;
    uint currentDepthHandle;
    uint historyColorHandle;
    uint historyDepthHandle;
    uint outputHandle;
    uint samplerHandle;
};

cbuffer TaaSettings : register(b1)
{
    uint renderSizeX;
    uint renderSizeY;
    uint historyValid;
    uint resetHistory;
};

float3 RGBToYCoCg(float3 color)
{
    float Y = dot(color, float3(0.25f, 0.5f, 0.25f));
    float Co = color.r - color.b;
    float Cg = color.g - 0.5f * (color.r + color.b);
    return float3(Y, Co, Cg);
}

float3 YCoCgToRGB(float3 color)
{
    float Y = color.x;
    float Co = color.y;
    float Cg = color.z;
    return float3(
        Y + 0.5f * Co - 0.5f * Cg,
        Y + 0.5f * Cg,
        Y - 0.5f * Co - 0.5f * Cg);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= renderSizeX || dispatchThreadId.y >= renderSizeY)
    {
        return;
    }

    Texture2D<float4> currentColorTexture = GetTexture(currentColorHandle);
    Texture2D<float4> velocityTexture = GetTexture(velocityHandle);
    Texture2D<float4> materialTexture = GetTexture(materialHandle);
    Texture2D<float4> currentDepthTexture = GetTexture(currentDepthHandle);
    Texture2D<float4> historyColorTexture = GetTexture(historyColorHandle);
    Texture2D<float4> historyDepthTexture = GetTexture(historyDepthHandle);
    RWTexture2D<float4> outputTexture = GetRWTexture(outputHandle);
    SamplerState linearSampler = GetSampler(samplerHandle);

    int2 pixel = int2(dispatchThreadId.xy);
    float2 renderSize = float2(renderSizeX, renderSizeY);
    float2 uv = (float2(dispatchThreadId.xy) + 0.5f) / renderSize;

    float3 currentColor = currentColorTexture.Load(int3(pixel, 0)).rgb;
    float2 velocity = velocityTexture.Load(int3(pixel, 0)).xy;
    float alphaTestMask = saturate(materialTexture.Load(int3(pixel, 0)).a);
    float currentDepth = currentDepthTexture.Load(int3(pixel, 0)).r;

    bool historyAllowed = historyValid != 0u && resetHistory == 0u;
    float2 prevUv = uv + (velocity / renderSize);
    bool historyRejected = !historyAllowed;
    historyRejected = historyRejected || any(prevUv < 0.0f.xx) || any(prevUv > 1.0f.xx);
    historyRejected = historyRejected || length(velocity) > max(renderSizeX, renderSizeY);

    float3 minBox = RGBToYCoCg(currentColor);
    float3 maxBox = minBox;
    for (int offsetY = -1; offsetY <= 1; offsetY++)
    {
        for (int offsetX = -1; offsetX <= 1; offsetX++)
        {
            int2 samplePixel = clamp(pixel + int2(offsetX, offsetY), int2(0, 0), int2(int(renderSizeX) - 1, int(renderSizeY) - 1));
            float3 sampleColor = currentColorTexture.Load(int3(samplePixel, 0)).rgb;
            float3 sampleYCoCg = RGBToYCoCg(sampleColor);
            minBox = min(minBox, sampleYCoCg);
            maxBox = max(maxBox, sampleYCoCg);
        }
    }

    float3 resolvedColor = currentColor;
    if (!historyRejected)
    {
        int2 historyPixel = clamp(int2(prevUv * renderSize), int2(0, 0), int2(int(renderSizeX) - 1, int(renderSizeY) - 1));
        float historyDepth = historyDepthTexture.Load(int3(historyPixel, 0)).r;
        float depthThreshold = lerp(0.01f, 0.06f, alphaTestMask);
        historyRejected = abs(historyDepth - currentDepth) > depthThreshold;
        if (!historyRejected)
        {
            float3 historyColor = historyColorTexture.SampleLevel(linearSampler, prevUv, 0.0f).rgb;
            float3 clampedHistory = clamp(RGBToYCoCg(historyColor), minBox, maxBox);
            float motionPixels = length(velocity);
            float opaqueHistoryWeight = lerp(0.9f, 0.2f, saturate(motionPixels * 0.5f));
            float alphaTestHistoryWeight = lerp(0.7f, 0.15f, saturate(motionPixels * 0.75f));
            float historyWeight = lerp(opaqueHistoryWeight, alphaTestHistoryWeight, alphaTestMask);
            resolvedColor = lerp(currentColor, YCoCgToRGB(clampedHistory), historyWeight);
        }
    }

    outputTexture[pixel] = float4(resolvedColor, 1.0f);
}
