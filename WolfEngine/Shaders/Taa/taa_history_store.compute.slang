import "../Common/common.slang";
import "../Common/common_bindless.slang";

cbuffer BindlessHandles : register(b0)
{
    uint sourceColorHandle;
    uint sourceDepthHandle;
    uint historyColorHandle;
    uint historyDepthHandle;
};

cbuffer CopySettings : register(b1)
{
    uint renderSizeX;
    uint renderSizeY;
    float projectionZBias;
    float projectionZScale;
};

/// TAA keeps history depth as view depth, zero for sky: the history target is a half float, which holds view
/// depth to about 0.1% at any distance but cannot resolve device depth near 1. projectionZBias and
/// projectionZScale are this frame's projection M33 and M43.
float HistoryViewDepth(float deviceDepth)
{
    if (IsBackgroundDepth(deviceDepth))
    {
        return 0.0f;
    }

    float denominator = deviceDepth - projectionZBias;
    if (abs(denominator) < 1e-6f)
    {
        denominator = denominator < 0.0f ? -1e-6f : 1e-6f;
    }
    return projectionZScale / denominator;
}

[numthreads(8, 8, 1)]
void TaaHistoryStoreCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= renderSizeX || dispatchThreadId.y >= renderSizeY)
    {
        return;
    }

    int2 pixel = int2(dispatchThreadId.xy);
    Texture2D<float4> sourceColor = GetTexture(sourceColorHandle);
    Texture2D<float4> sourceDepth = GetTexture(sourceDepthHandle);
    RWTexture2D<float4> historyColor = GetRWTexture(historyColorHandle);
    RWTexture2D<float4> historyDepth = GetRWTexture(historyDepthHandle);

    historyColor[pixel] = sourceColor.Load(int3(pixel, 0));
    float viewDepth = HistoryViewDepth(sourceDepth.Load(int3(pixel, 0)).r);
    historyDepth[pixel] = float4(viewDepth, viewDepth, viewDepth, 1.0f);
}
