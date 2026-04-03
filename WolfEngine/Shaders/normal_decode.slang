interface INormalMapDecoder
{
    float3 Decode(float4 normalSample);
}

struct Bc5NormalMapDecoder : INormalMapDecoder
{
    float3 Decode(float4 normalSample)
    {
        float2 normalXy = normalSample.rg * 2.0f - 1.0f;
        float normalZ = sqrt(max(1.0f - dot(normalXy, normalXy), 0.0f));
        return normalize(float3(normalXy, normalZ));
    }
}

struct RgbNormalMapDecoder : INormalMapDecoder
{
    float3 Decode(float4 normalSample)
    {
        return normalize(normalSample.rgb * 2.0f - 1.0f);
    }
}

float3 DecodeNormalMapSample(float4 normalSample)
{
#if defined(WOLF_TARGET_D3D12)
    Bc5NormalMapDecoder decoder;
#else
    RgbNormalMapDecoder decoder;
#endif

    return decoder.Decode(normalSample);
}
