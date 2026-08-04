import "../Common/common_bindless.slang";

cbuffer BindlessHandles : register(b0)
{
    uint sourceAlbedoHandle;
    uint sourceNormalHandle;
    uint sourceMaterialHandle;
    uint sourceEmissiveHandle;
    uint targetAlbedoHandle;
    uint targetNormalHandle;
    uint targetMaterialHandle;
    uint targetEmissiveHandle;
};

cbuffer CopySettings : register(b1)
{
    uint renderSizeX;
    uint renderSizeY;
};

[numthreads(8, 8, 1)]
void GBufferDecalSeedCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= renderSizeX || dispatchThreadId.y >= renderSizeY)
    {
        return;
    }

    int2 pixel = int2(dispatchThreadId.xy);
    Texture2D<float4> sourceAlbedo = GetTexture(sourceAlbedoHandle);
    Texture2D<float4> sourceNormal = GetTexture(sourceNormalHandle);
    Texture2D<float4> sourceMaterial = GetTexture(sourceMaterialHandle);
    Texture2D<float4> sourceEmissive = GetTexture(sourceEmissiveHandle);
    RWTexture2D<float4> targetAlbedo = GetRWTexture(targetAlbedoHandle);
    RWTexture2D<float4> targetNormal = GetRWTexture(targetNormalHandle);
    RWTexture2D<float4> targetMaterial = GetRWTexture(targetMaterialHandle);
    RWTexture2D<float4> targetEmissive = GetRWTexture(targetEmissiveHandle);

    targetAlbedo[pixel] = sourceAlbedo.Load(int3(pixel, 0));
    targetNormal[pixel] = sourceNormal.Load(int3(pixel, 0));
    targetMaterial[pixel] = sourceMaterial.Load(int3(pixel, 0));
    targetEmissive[pixel] = sourceEmissive.Load(int3(pixel, 0));
}
