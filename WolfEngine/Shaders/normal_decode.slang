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
    Bc5NormalMapDecoder decoder;

    return decoder.Decode(normalSample);
}

float3 ApplyNormalMapScale(float3 tangentNormal, float normalScale)
{
    tangentNormal.xy *= normalScale;
    return normalize(tangentNormal);
}
