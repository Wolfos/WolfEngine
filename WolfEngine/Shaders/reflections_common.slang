import "common_bindless.slang";

// Both reflection tracers write radiance plus a replacement weight into the reflection
// buffer. Deferred lighting turns that radiance into specular light, so the tracers must
// not composite anything themselves.
static const uint REFLECTION_MAX_COLOR_PYRAMID_LEVELS = 8;

float ReflectionRoughnessFade(float roughness, float maxRoughness)
{
    return saturate(1.0f - roughness / max(maxRoughness, 1e-4f));
}

/// Fetches shaded radiance for a screen-space hit from the previous frame's color pyramid.
/// The hit is reprojected through its own motion vector, and the pyramid level is chosen from
/// the reflector's roughness so glossy lobes read pre-filtered color instead of a single tap.
float3 SampleReflectionColorPyramid(
    uint colorPyramidHandles[REFLECTION_MAX_COLOR_PYRAMID_LEVELS],
    uint colorPyramidLevelCount,
    uint samplerHandle,
    uint velocityHandle,
    uint2 framebufferSize,
    float2 hitUv,
    float roughness,
    out float confidence)
{
    confidence = 0.0f;
    uint levelCount = clamp(colorPyramidLevelCount, 1u, REFLECTION_MAX_COLOR_PYRAMID_LEVELS);
    uint2 hitPixel = min(uint2(hitUv * float2(framebufferSize)), framebufferSize - 1u);
    float2 velocity = GetTexture(velocityHandle).Load(int3(hitPixel, 0)).xy;
    float2 historyUv = hitUv + velocity / float2(framebufferSize);
    if (any(historyUv < 0.0f.xx) || any(historyUv > 1.0f.xx))
    {
        // The hit surface was off screen last frame, so there is no shaded color to reuse.
        return 0.0f.xxx;
    }

    float level = saturate(roughness) * float(levelCount - 1u);
    uint lowLevel = uint(floor(level));
    uint highLevel = min(lowLevel + 1u, levelCount - 1u);
    SamplerState linearSampler = GetSampler(samplerHandle);
    float3 low = GetTexture(colorPyramidHandles[lowLevel])
        .SampleLevel(linearSampler, historyUv, 0.0f).rgb;
    float3 high = GetTexture(colorPyramidHandles[highLevel])
        .SampleLevel(linearSampler, historyUv, 0.0f).rgb;
    confidence = 1.0f;
    return max(lerp(low, high, level - float(lowLevel)), 0.0f.xxx);
}
