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
    float closestDepth = currentDepthTexture.Load(int3(pixel, 0)).r;
    int2 closestPixel = pixel;
    float3 minBox = RGBToYCoCg(currentColor);
    float3 maxBox = minBox;

    for (int offsetY = -1; offsetY <= 1; offsetY++)
    {
        for (int offsetX = -1; offsetX <= 1; offsetX++)
        {
            int2 samplePixel = clamp(pixel + int2(offsetX, offsetY), int2(0, 0), int2(int(renderSizeX) - 1, int(renderSizeY) - 1));
            float sampleDepth = currentDepthTexture.Load(int3(samplePixel, 0)).r;
            if (sampleDepth < closestDepth)
            {
                closestDepth = sampleDepth;
                closestPixel = samplePixel;
            }

            float3 sampleColor = currentColorTexture.Load(int3(samplePixel, 0)).rgb;
            float3 sampleYCoCg = RGBToYCoCg(sampleColor);
            minBox = min(minBox, sampleYCoCg);
            maxBox = max(maxBox, sampleYCoCg);
        }
    }

    float2 velocity = velocityTexture.Load(int3(closestPixel, 0)).xy;
    float alphaTestMask = saturate(materialTexture.Load(int3(closestPixel, 0)).a);
    float currentDepth = closestDepth;
    float motionPixels = length(velocity);

    bool historyAllowed = historyValid != 0u && resetHistory == 0u;
    float2 prevUv = uv + (velocity / renderSize);
    bool historyRejected = !historyAllowed;
    historyRejected = historyRejected || any(prevUv < 0.0f.xx) || any(prevUv > 1.0f.xx);
    historyRejected = historyRejected || motionPixels > max(renderSizeX, renderSizeY);

    float3 resolvedColor = currentColor;
    if (!historyRejected)
    {
        int2 historyPixel = clamp(int2(prevUv * renderSize), int2(0, 0), int2(int(renderSizeX) - 1, int(renderSizeY) - 1));
        float historyDepth = historyDepthTexture.Load(int3(historyPixel, 0)).r;
        for (int offsetY = -1; offsetY <= 1; offsetY++)
        {
            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                int2 samplePixel = clamp(historyPixel + int2(offsetX, offsetY), int2(0, 0), int2(int(renderSizeX) - 1, int(renderSizeY) - 1));
                historyDepth = min(historyDepth, historyDepthTexture.Load(int3(samplePixel, 0)).r);
            }
        }

        float depthThreshold = lerp(0.02f, 0.075f, alphaTestMask);
        historyRejected = abs(historyDepth - currentDepth) > depthThreshold;
        if (!historyRejected)
        {
            float3 center = 0.5f * (minBox + maxBox);
            float3 extent = 0.5f * (maxBox - minBox);
            float clampExpansion = lerp(2.5f, 1.25f, saturate(motionPixels * 0.5f));
            extent = max(extent * clampExpansion, float3(0.02f, 0.02f, 0.02f));
            float3 clampMin = center - extent;
            float3 clampMax = center + extent;

            float3 historyColor = historyColorTexture.SampleLevel(linearSampler, prevUv, 0.0f).rgb;
            float3 clampedHistory = clamp(RGBToYCoCg(historyColor), clampMin, clampMax);
            float opaqueHistoryWeight = lerp(0.97f, 0.2f, saturate(motionPixels * 0.5f));
            float alphaTestHistoryWeight = lerp(0.9f, 0.15f, saturate(motionPixels * 0.75f));
            float historyWeight = lerp(opaqueHistoryWeight, alphaTestHistoryWeight, alphaTestMask);
            resolvedColor = lerp(currentColor, YCoCgToRGB(clampedHistory), historyWeight);
        }
    }

    outputTexture[pixel] = float4(resolvedColor, 1.0f);
}
