import "../Common/common_bindless.slang";
import "../ThirdParty/Aces/aces2_sdr_output.slang";

cbuffer BindlessHandles : register(b0)
{
    uint inputHandle;
    uint outputHandle;
    uint samplerHandle;
};

cbuffer TonemappingSettings : register(b1)
{
    uint renderSizeX;
    uint renderSizeY;
    float exposure;
    float _padding0;
};

[numthreads(8, 8, 1)]
void TonemappingCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= renderSizeX || dispatchThreadId.y >= renderSizeY)
    {
        return;
    }

    Texture2D<float4> inputTexture = GetTexture(inputHandle);
    RWTexture2D<float4> outputTexture = GetRWTexture(outputHandle);
    SamplerState linearSampler = GetSampler(samplerHandle);

    float2 renderSize = float2(renderSizeX, renderSizeY);
    float2 uv = (float2(dispatchThreadId.xy) + 0.5f) / renderSize;
    float3 sceneHdr = inputTexture.SampleLevel(linearSampler, uv, 0.0f).rgb;
    float3 exposedSceneHdr = max(sceneHdr * exposure, 0.0f.xxx);
    float3 linearDisplaySrgb = Aces2SdrOutputTransform(exposedSceneHdr);
    outputTexture[dispatchThreadId.xy] = float4(linearDisplaySrgb, 1.0f);
}
