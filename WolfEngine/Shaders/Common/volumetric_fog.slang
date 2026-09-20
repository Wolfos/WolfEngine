import "common_bindless.slang";

float FogSliceBoundary(uint slice, uint sliceCount, float nearPlane, float maxDistance)
{
    float t = float(slice) / max(float(sliceCount), 1.0f);
    return nearPlane * pow(maxDistance / nearPlane, t);
}

float FogDepthToVolumeW(float viewDepth, uint sliceCount, float nearPlane, float maxDistance)
{
    float t = log(clamp(viewDepth, nearPlane, maxDistance) / nearPlane) /
        max(log(maxDistance / nearPlane), 1e-6f);
    return (t * float(sliceCount) - 0.5f) / max(float(sliceCount), 1.0f);
}

float4 SampleIntegratedFog(uint fogHandle, uint samplerHandle, float2 uv, float viewDepth,
    uint sliceCount, float nearPlane, float maxDistance)
{
    float w = viewDepth >= maxDistance
        ? (float(sliceCount) - 0.5f) / float(sliceCount)
        : FogDepthToVolumeW(viewDepth, sliceCount, nearPlane, maxDistance);
    return GetTexture3D(fogHandle).SampleLevel(
        GetSampler(samplerHandle), float3(saturate(uv), saturate(w)), 0.0f);
}
