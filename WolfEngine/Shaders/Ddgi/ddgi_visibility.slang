static const float DDGI_VISIBILITY_VARIANCE_FLOOR = 0.000001f;
static const float DDGI_WEIGHT_CRUSH_THRESHOLD = 0.2f;
static const float DDGI_MIN_CONFIDENT_WEIGHT_RATIO = 0.01f;

/// Drives small backface/visibility weights toward zero so a probe that is
/// mostly rejected cannot contribute faint light (Majercik et al. 2019).
float DdgiCrushProbeWeight(float weight)
{
    const float threshold = DDGI_WEIGHT_CRUSH_THRESHOLD;
    return weight < threshold
        ? weight * weight * weight / (threshold * threshold)
        : weight;
}

/// Normalising by the surviving weight scales a few barely-visible probes up to
/// full intensity. When almost all of the unoccluded weight was rejected, fade
/// the blend out instead of trusting what leaked through.
float DdgiSampleConfidence(float weightSum, float baseWeightSum)
{
    return saturate(weightSum / max(baseWeightSum * DDGI_MIN_CONFIDENT_WEIGHT_RATIO, 1e-5f));
}

float2 DdgiBlendVisibilityMoments(float2 allMoments, float2 hitMoments, float hitCoverage)
{
    // A thin wall and a sky miss form two depth populations. Blending their raw
    // moments introduces a large between-population variance, which makes the
    // Chebyshev visibility test accept light from probes across the wall. Blend
    // the means and within-population variances instead.
    float blend = smoothstep(0.15f, 0.5f, saturate(hitCoverage));
    float allVariance = max(allMoments.y - allMoments.x * allMoments.x, 0.0f);
    float hitVariance = max(hitMoments.y - hitMoments.x * hitMoments.x, 0.0f);
    float meanDistance = lerp(allMoments.x, hitMoments.x, blend);
    float variance = lerp(allVariance, hitVariance, blend);
    return float2(meanDistance, meanDistance * meanDistance + variance);
}

float DdgiEvaluateVisibility(float meanDistance, float meanDistanceSquared, float receiverDistance)
{
    if (receiverDistance <= meanDistance)
    {
        return 1.0f;
    }

    float variance = max(meanDistanceSquared - meanDistance * meanDistance, DDGI_VISIBILITY_VARIANCE_FLOOR);
    float distanceDelta = receiverDistance - meanDistance;
    float chebyshevVisibility = variance / (variance + distanceDelta * distanceDelta);
    return saturate(chebyshevVisibility * chebyshevVisibility * chebyshevVisibility);
}
