static const float DDGI_VISIBILITY_VARIANCE_FLOOR = 0.000001f;

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
