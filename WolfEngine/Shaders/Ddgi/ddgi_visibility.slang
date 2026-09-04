static const float DDGI_VISIBILITY_VARIANCE_FLOOR = 0.000001f;

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
