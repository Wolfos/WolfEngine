import "common_bindless.slang";

cbuffer BindlessHandles : register(b0)
{
    uint inputHandle;
    uint outputHandle;
};

cbuffer CopySettings : register(b1)
{
    uint renderSizeX;
    uint renderSizeY;
};

[numthreads(8, 8, 1)]
void CopyToFinalCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= renderSizeX || dispatchThreadId.y >= renderSizeY)
    {
        return;
    }

    Texture2D<float4> inputTexture = GetTexture(inputHandle);
    RWTexture2D<float4> outputTexture = GetRWTexture(outputHandle);
    int2 pixel = int2(dispatchThreadId.xy);
    outputTexture[pixel] = inputTexture.Load(int3(pixel, 0));
}
