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
    float opaqueDepthThreshold;
    float alphaTestDepthThreshold;
    float opaqueClampSigma;
    float alphaTestClampSigma;
    float lowMotionClampExpansion;
    float highMotionClampExpansion;
    float clampExpansionMotionScale;
    float lowMotionOpaqueHistoryWeight;
    float highMotionOpaqueHistoryWeight;
    float opaqueHistoryMotionScale;
    float lowMotionAlphaTestHistoryWeight;
    float highMotionAlphaTestHistoryWeight;
    float alphaTestHistoryMotionScale;
    float _taaPadding0;
    float _taaPadding1;
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

float MaxComponent(float3 value)
{
    return max(value.x, max(value.y, value.z));
}

float4 SampleCatmullRom(Texture2D<float4> texture, SamplerState samplerState, float2 uv, float2 textureSize)
{
    float2 samplePos = uv * textureSize;
    float2 texPos1 = floor(samplePos - 0.5f) + 0.5f;
    float2 f = samplePos - texPos1;

    float2 w0 = f * (-0.5f + f * (1.0f - 0.5f * f));
    float2 w1 = 1.0f + f * f * (-2.5f + 1.5f * f);
    float2 w2 = f * (0.5f + f * (2.0f - 1.5f * f));
    float2 w3 = f * f * (-0.5f + 0.5f * f);

    float2 w12 = w1 + w2;
    float2 offset12 = w2 / max(w12, 1e-5f.xx);

    float2 texPos0 = (texPos1 - 1.0f) / textureSize;
    float2 texPos3 = (texPos1 + 2.0f) / textureSize;
    float2 texPos12 = (texPos1 + offset12) / textureSize;

    return texture.SampleLevel(samplerState, float2(texPos0.x, texPos0.y), 0.0f) * w0.x * w0.y +
        texture.SampleLevel(samplerState, float2(texPos12.x, texPos0.y), 0.0f) * w12.x * w0.y +
        texture.SampleLevel(samplerState, float2(texPos3.x, texPos0.y), 0.0f) * w3.x * w0.y +
        texture.SampleLevel(samplerState, float2(texPos0.x, texPos12.y), 0.0f) * w0.x * w12.y +
        texture.SampleLevel(samplerState, float2(texPos12.x, texPos12.y), 0.0f) * w12.x * w12.y +
        texture.SampleLevel(samplerState, float2(texPos3.x, texPos12.y), 0.0f) * w3.x * w12.y +
        texture.SampleLevel(samplerState, float2(texPos0.x, texPos3.y), 0.0f) * w0.x * w3.y +
        texture.SampleLevel(samplerState, float2(texPos12.x, texPos3.y), 0.0f) * w12.x * w3.y +
        texture.SampleLevel(samplerState, float2(texPos3.x, texPos3.y), 0.0f) * w3.x * w3.y;
}

[numthreads(8, 8, 1)]
void TaaResolveCS(uint3 dispatchThreadId : SV_DispatchThreadID)
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
    float2 centerUv = (float2(pixel) + float2(0.5f, 0.5f)) / renderSize;
    float3 centerColor = currentColorTexture.Load(int3(pixel, 0)).rgb;
    float centerDepth = currentDepthTexture.Load(int3(pixel, 0)).r;
    float closestDepth = centerDepth;
    int2 closestPixel = pixel;
    float3 minBox = RGBToYCoCg(centerColor);
    float3 maxBox = minBox;
    float3 currentColor = centerColor;
    float3 firstMoment = 0.0f.xxx;
    float3 secondMoment = 0.0f.xxx;

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
            firstMoment += sampleYCoCg;
            secondMoment += sampleYCoCg * sampleYCoCg;
        }
    }

    float3 mean = firstMoment / 9.0f;
    float3 variance = max(secondMoment / 9.0f - mean * mean, 0.0f.xxx);
    float3 sigma = sqrt(variance);

    float4 velocitySample = velocityTexture.Load(int3(closestPixel, 0));
    float2 velocity = velocitySample.xy;
    float expectedHistoryDepth = velocitySample.z;
    float alphaTestMask = saturate(materialTexture.Load(int3(closestPixel, 0)).a);
    float motionPixels = length(velocity);
    float2 closestUv = (float2(closestPixel) + float2(0.5f, 0.5f)) / renderSize;
    float depthDiscontinuity = abs(closestDepth - centerDepth);
    float closestSampleWeight = smoothstep(0.0f, 0.02f, depthDiscontinuity) * smoothstep(0.35f, 1.5f, motionPixels);
    float2 reprojectionBaseUv = lerp(centerUv, closestUv, closestSampleWeight);

    bool historyAllowed = historyValid != 0u && resetHistory == 0u;
    float2 prevUv = reprojectionBaseUv + (velocity / renderSize);
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

        float depthThreshold = lerp(opaqueDepthThreshold, alphaTestDepthThreshold, alphaTestMask);
        historyRejected = abs(historyDepth - expectedHistoryDepth) > depthThreshold;
        if (!historyRejected)
        {
            float sigmaScale = lerp(opaqueClampSigma, alphaTestClampSigma, alphaTestMask);
            float3 varianceMin = mean - sigmaScale * sigma;
            float3 varianceMax = mean + sigmaScale * sigma;

            float3 clampMin = min(max(minBox, varianceMin), min(maxBox, varianceMax));
            float3 clampMax = max(max(minBox, varianceMin), min(maxBox, varianceMax));

            float3 center = 0.5f * (clampMin + clampMax);
            float3 extent = 0.5f * (clampMax - clampMin);
            float clampExpansion = lerp(
                lowMotionClampExpansion,
                highMotionClampExpansion,
                saturate(motionPixels * clampExpansionMotionScale));
            extent = max(extent * clampExpansion, float3(0.01f, 0.01f, 0.01f));
            clampMin = center - extent;
            clampMax = center + extent;

            float3 historyColor = SampleCatmullRom(historyColorTexture, linearSampler, prevUv, renderSize).rgb;
            float3 historyYCoCg = RGBToYCoCg(historyColor);
            float3 clampedHistoryYCoCg = clamp(historyYCoCg, clampMin, clampMax);
            float3 clampedHistoryRgb = YCoCgToRGB(clampedHistoryYCoCg);

            // If the reprojected sample lands far outside the current neighborhood, respond faster.
            // Keep this mostly motion-driven so static high-frequency detail can still accumulate.
            float3 clampRange = max(clampMax - clampMin, float3(0.02f, 0.02f, 0.02f));
            float3 normalizedClampDelta = abs(clampedHistoryYCoCg - historyYCoCg) / clampRange;
            float clampAmount = MaxComponent(normalizedClampDelta);
            float clampResponse = smoothstep(0.2f, 0.9f, clampAmount);
            float historyConfidence = 1.0f - clampResponse * saturate(motionPixels * 0.35f + alphaTestMask * 0.5f);

            float opaqueHistoryWeight = lerp(
                lowMotionOpaqueHistoryWeight,
                highMotionOpaqueHistoryWeight,
                saturate(motionPixels * opaqueHistoryMotionScale));
            float alphaTestHistoryWeight = lerp(
                lowMotionAlphaTestHistoryWeight,
                highMotionAlphaTestHistoryWeight,
                saturate(motionPixels * alphaTestHistoryMotionScale));
            float historyWeight = lerp(opaqueHistoryWeight, alphaTestHistoryWeight, alphaTestMask) * historyConfidence;

            resolvedColor = lerp(currentColor, clampedHistoryRgb, historyWeight);
        }
    }

    outputTexture[pixel] = float4(resolvedColor, 1.0f);
}
