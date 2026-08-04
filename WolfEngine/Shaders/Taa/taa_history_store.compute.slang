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
    uint _copyPadding0;
    uint _copyPadding1;
};

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
    float depth = sourceDepth.Load(int3(pixel, 0)).r;
    historyDepth[pixel] = float4(depth, depth, depth, 1.0f);
}
