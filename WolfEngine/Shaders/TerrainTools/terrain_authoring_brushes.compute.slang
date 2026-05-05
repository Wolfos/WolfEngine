import "../common_bindless.slang";

cbuffer BindlessHandles : register(b0)
{
    uint inputHandle;
    uint outputHandle;
};

cbuffer TerrainBrushParameters : register(b1)
{
    uint textureWidth;
    uint textureHeight;
    uint layerIndex;
    float brushStrength;
    float brushFalloff;
    float brushInvertSign;
    float flattenHeightNormalized;
    float2 brushCenterPixels;
    float2 brushRadiusPixels;
};

float ComputeBrushWeight(int2 pixel)
{
    float2 safeRadius = max(brushRadiusPixels, float2(0.001f, 0.001f));
    float2 normalizedOffset = (float2(pixel) - brushCenterPixels) / safeRadius;
    float distanceToCenter = length(normalizedOffset);
    if (distanceToCenter >= 1.0f)
    {
        return 0.0f;
    }

    return pow(1.0f - distanceToCenter, max(brushFalloff, 0.1f));
}

int2 ClampPixel(int2 pixel)
{
    return clamp(pixel, int2(0, 0), int2(int(textureWidth) - 1, int(textureHeight) - 1));
}

float SampleHeight(Texture2D<float4> inputTexture, int2 pixel)
{
    return inputTexture.Load(int3(ClampPixel(pixel), 0)).r;
}

float SampleAverageHeight(Texture2D<float4> inputTexture, int2 pixel)
{
    float total = 0.0f;
    uint count = 0;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            total += SampleHeight(inputTexture, pixel + int2(x, y));
            count++;
        }
    }

    return count > 0 ? total / count : 0.0f;
}

void InitializeBrushIO(
    uint3 dispatchThreadId,
    out Texture2D<float4> inputTexture,
    out RWTexture2D<float4> outputTexture,
    out int2 pixel)
{
    inputTexture = GetTexture(inputHandle);
    outputTexture = GetRWTexture(outputHandle);
    pixel = int2(dispatchThreadId.xy);
}

[numthreads(8, 8, 1)]
void ApplyHeightmapRaiseLowerBrush(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= textureWidth || dispatchThreadId.y >= textureHeight)
    {
        return;
    }

    Texture2D<float4> inputTexture;
    RWTexture2D<float4> outputTexture;
    int2 pixel;
    InitializeBrushIO(dispatchThreadId, inputTexture, outputTexture, pixel);

    float4 current = inputTexture.Load(int3(pixel, 0));
    float weight = ComputeBrushWeight(pixel);
    float delta = brushStrength * weight * brushInvertSign * 0.25f;
    current.r = saturate(current.r + delta);
    outputTexture[pixel] = current;
}

[numthreads(8, 8, 1)]
void ApplyHeightmapFlattenBrush(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= textureWidth || dispatchThreadId.y >= textureHeight)
    {
        return;
    }

    Texture2D<float4> inputTexture;
    RWTexture2D<float4> outputTexture;
    int2 pixel;
    InitializeBrushIO(dispatchThreadId, inputTexture, outputTexture, pixel);

    float4 current = inputTexture.Load(int3(pixel, 0));
    float weight = ComputeBrushWeight(pixel);
    current.r = saturate(lerp(current.r, flattenHeightNormalized, brushStrength * weight));
    outputTexture[pixel] = current;
}

[numthreads(8, 8, 1)]
void ApplyHeightmapSmoothBrush(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= textureWidth || dispatchThreadId.y >= textureHeight)
    {
        return;
    }

    Texture2D<float4> inputTexture;
    RWTexture2D<float4> outputTexture;
    int2 pixel;
    InitializeBrushIO(dispatchThreadId, inputTexture, outputTexture, pixel);

    float4 current = inputTexture.Load(int3(pixel, 0));
    float weight = ComputeBrushWeight(pixel);
    float smoothedHeight = SampleAverageHeight(inputTexture, pixel);
    current.r = saturate(lerp(current.r, smoothedHeight, brushStrength * weight));
    outputTexture[pixel] = current;
}

float4 NormalizeWeights(float4 weights)
{
    float sum = weights.x + weights.y + weights.z + weights.w;
    if (sum <= 1e-5f)
    {
        return float4(1.0f, 0.0f, 0.0f, 0.0f);
    }

    return weights / sum;
}

float4 PaintTowardLayer(float4 current, uint layerIndex, float amount)
{
    float4 target = 0.0f;

    if (layerIndex == 0u) target.r = 1.0f;
    if (layerIndex == 1u) target.g = 1.0f;
    if (layerIndex == 2u) target.b = 1.0f;
    if (layerIndex == 3u) target.a = 1.0f;

    return NormalizeWeights(lerp(current, target, saturate(amount)));
}

[numthreads(8, 8, 1)]
void ApplyLayerMapLayerBrush(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= textureWidth || dispatchThreadId.y >= textureHeight)
    {
        return;
    }

    Texture2D<float4> inputTexture;
    RWTexture2D<float4> outputTexture;
    int2 pixel;
    InitializeBrushIO(dispatchThreadId, inputTexture, outputTexture, pixel);

    float4 current = inputTexture.Load(int3(pixel, 0));
    float weight = ComputeBrushWeight(pixel);
    float delta = brushStrength * weight * brushInvertSign;
    uint clampedLayerIndex = min(layerIndex, 3u);

    outputTexture[pixel] = PaintTowardLayer(current, clampedLayerIndex, delta);
}
