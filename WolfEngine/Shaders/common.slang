struct CameraParams
{
    float4x4 invProjection;
    float3 cameraOrigin; // world-space origin if needed
    float padding; // align to 16 bytes
};

float3 ReconstructViewPosition(uint2 pixel, uint2 size, float depth, float4x4 invProjection)
{
    // Map pixel center to NDC [-1,1], Y flipped for typical render target orientation
    float2 ndc;
    ndc.x = (float(pixel.x) + 0.5f) / float(size.x) * 2.0f - 1.0f;
    ndc.y = -((float(pixel.y) + 0.5f) / float(size.y) * 2.0f - 1.0f);

    float4 clip = float4(ndc, depth, 1.0f);
    float4 viewPosH = mul(clip, invProjection);
    return viewPosH.xyz / viewPosH.w;
}

float3 ComputeViewDir(float3 viewPos)
{
    return normalize(-viewPos);
}

float3 ViewToWorld(float3 viewPos, float3 cameraOrigin)
{
    return viewPos + cameraOrigin;
}
