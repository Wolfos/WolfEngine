import "common_bindless.slang";

// Both reflection tracers write radiance plus a replacement weight into the reflection
// buffer. Deferred lighting turns that radiance into specular light, so the tracers must
// not composite anything themselves.
static const uint REFLECTION_MAX_COLOR_PYRAMID_LEVELS = 8;

float ReflectionRoughnessFade(float roughness, float maxRoughness)
{
    return saturate(1.0f - roughness / max(maxRoughness, 1e-4f));
}

/// Fades a screen-space fetch out as it approaches the edge of the frame. Returns zero for uvs
/// outside the frame, so callers can use this in place of a bounds test and get a ramp instead
/// of a cliff.
float ReflectionScreenEdgeWeight(float2 uv, float edgeFade)
{
    float distanceToEdge = min(min(uv.x, 1.0f - uv.x), min(uv.y, 1.0f - uv.y));
    return saturate(distanceToEdge / max(edgeFade, 1e-4f));
}

/// Pyramid level needed to sample the reflected image without aliasing, from how far apart
/// neighbouring pixels land in the pyramid. Roughness alone is not enough: a mirror is roughness
/// zero, but a curved one compresses a wide swath of the scene into a few pixels, and fetching
/// that at full resolution aliases. The pattern is fixed while the camera is still and crawls
/// once it moves, which is the wobble.
float ReflectionFootprintLevel(
    float2 hitUv,
    float2 neighbourHitUvX,
    float2 neighbourHitUvY,
    uint2 framebufferSize)
{
    float2 deltaX = (neighbourHitUvX - hitUv) * float2(framebufferSize);
    float2 deltaY = (neighbourHitUvY - hitUv) * float2(framebufferSize);
    float spreadInPixels = max(length(deltaX), length(deltaY));
    return log2(max(spreadInPixels, 1.0f));
}

/// Fetches shaded radiance for a screen-space hit from the previous frame's color pyramid.
/// The hit is reprojected through its own motion vector, and the pyramid level is chosen from
/// the reflector's roughness so glossy lobes read pre-filtered color instead of a single tap.
/// Confidence ramps down as the reprojection leaves the frame rather than cutting off, because
/// a per-pixel binary decision here reads as a crawling, wobbling seam once the camera moves.
float3 SampleReflectionColorPyramid(
    uint colorPyramidHandles[REFLECTION_MAX_COLOR_PYRAMID_LEVELS],
    uint colorPyramidLevelCount,
    uint samplerHandle,
    uint velocityHandle,
    uint2 framebufferSize,
    float2 hitUv,
    float roughness,
    float edgeFade,
    float footprintLevel,
    float reprojectionStrength,
    out float confidence)
{
    confidence = 0.0f;
    uint levelCount = clamp(colorPyramidLevelCount, 1u, REFLECTION_MAX_COLOR_PYRAMID_LEVELS);
    uint2 hitPixel = min(uint2(saturate(hitUv) * float2(framebufferSize)), framebufferSize - 1u);
    float2 velocity = GetTexture(velocityHandle).Load(int3(hitPixel, 0)).xy;
    float2 historyUv = hitUv + velocity * reprojectionStrength / float2(framebufferSize);
    float historyEdgeWeight = ReflectionScreenEdgeWeight(historyUv, edgeFade);
    if (historyEdgeWeight <= 0.0f)
    {
        // The hit surface was off screen last frame, so there is no shaded color to reuse.
        return 0.0f.xxx;
    }

    float level = clamp(
        max(saturate(roughness) * float(levelCount - 1u), footprintLevel),
        0.0f,
        float(levelCount - 1u));
    uint lowLevel = uint(floor(level));
    uint highLevel = min(lowLevel + 1u, levelCount - 1u);
    SamplerState linearSampler = GetSampler(samplerHandle);
    float3 low = GetTexture(colorPyramidHandles[lowLevel])
        .SampleLevel(linearSampler, historyUv, 0.0f).rgb;
    float3 high = GetTexture(colorPyramidHandles[highLevel])
        .SampleLevel(linearSampler, historyUv, 0.0f).rgb;
    confidence = historyEdgeWeight;
    return max(lerp(low, high, level - float(lowLevel)), 0.0f.xxx);
}
