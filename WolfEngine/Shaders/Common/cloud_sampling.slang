import "common_bindless.slang";

/// Distance the cloud passes store for pixels with no opaque geometry (km). Both the trace and the full-resolution
/// pixel use it, so background pixels always match.
static const float CLOUD_BACKGROUND_DISTANCE_KM = 10000.0f;

static const uint CLOUD_COMPOSITE_ENABLED = 1u;
static const uint CLOUD_SHADOWS_ENABLED = 2u;

/// What a pass that composites the clouds or receives their shadow needs. Four scalars, then float4s, so the
/// layout is the same on D3D12 and Metal.
struct CloudComposite
{
    /// CLOUD_COMPOSITE_ENABLED | CLOUD_SHADOWS_ENABLED, or zero outside the Procedural sky.
    uint flags;
    /// rgb: cloud radiance with the atmosphere in front folded in. a: transmittance.
    uint colorHandle;
    /// x: cloud distance (km). y: the scene distance the trace pixel was clipped to (km).
    uint depthHandle;
    /// r: sunlight transmittance through the layer.
    uint shadowHandle;
    /// xy: cloud trace size.
    float4 trace;
    /// xy: shadow map centre (camera-relative XZ, m). z: 1 / shadow map extent (1/m).
    /// w: cloud base height relative to the camera (m).
    float4 shadowMap;
    /// xyz: direction toward the sun (world).
    float4 sunDirection;
};

bool CloudCompositeEnabled(CloudComposite composite)
{
    return (composite.flags & CLOUD_COMPOSITE_ENABLED) != 0u;
}

bool CloudShadowsEnabled(CloudComposite composite)
{
    return (composite.flags & CLOUD_SHADOWS_ENABLED) != 0u;
}

/// The reduced-resolution clouds at a full-resolution pixel. Bilinear taps are weighted by how closely the scene
/// depth each was traced against matches this pixel's, so clouds traced past a silhouette do not bleed onto it.
/// rgb is radiance and a transmittance; composite as color * a + rgb.
float4 SampleCloudsUpsampled(CloudComposite composite, float2 uv, float pixelDistanceKm)
{
    Texture2D<float4> colorTexture = GetTexture(composite.colorHandle);
    Texture2D<float4> depthTexture = GetTexture(composite.depthHandle);
    int2 size = int2(composite.trace.xy);
    float2 position = uv * composite.trace.xy - 0.5f;
    int2 origin = int2(floor(position));
    float2 fraction = position - float2(origin);

    float4 sum = 0.0f;
    float weightSum = 0.0f;
    float closestDifference = 1e30f;
    float4 closestColor = float4(0.0f, 0.0f, 0.0f, 1.0f);
    float closestCloudDistance = 0.0f;
    for (uint i = 0u; i < 4u; i++)
    {
        int2 offset = int2(i & 1u, i >> 1u);
        int2 coordinate = clamp(origin + offset, int2(0, 0), size - 1);
        float4 color = colorTexture.Load(int3(coordinate, 0));
        float2 depth = depthTexture.Load(int3(coordinate, 0)).xy;

        float2 bilinear = lerp(1.0f - fraction, fraction, float2(offset));
        float difference = abs(depth.y - pixelDistanceKm) / max(pixelDistanceKm, 1e-3f);
        float weight = bilinear.x * bilinear.y * exp2(-16.0f * difference);
        sum += color * weight;
        weightSum += weight;

        if (difference < closestDifference)
        {
            closestDifference = difference;
            closestColor = color;
            closestCloudDistance = depth.x;
        }
    }

    if (weightSum > 1e-4f)
    {
        return sum / weightSum;
    }

    // No tap was traced against this pixel's depth, as on a thin silhouette. A cloud further away than the
    // pixel is hidden by it; a nearer one is in front of it either way.
    if (pixelDistanceKm < closestCloudDistance * 0.9f)
    {
        return float4(0.0f, 0.0f, 0.0f, 1.0f);
    }

    return closestColor;
}

/// Fraction of sunlight the clouds let through to a camera-relative position. One where the shadow map does not
/// reach, fading out toward its edge.
float SampleCloudShadow(CloudComposite composite, uint samplerHandle, float3 position)
{
    if (CloudShadowsEnabled(composite) == false)
    {
        return 1.0f;
    }

    float3 sunDirection = composite.sunDirection.xyz;
    if (sunDirection.y <= 0.01f)
    {
        return 1.0f;
    }

    // Follow the sun ray up to the cloud base, where the map's columns start.
    float distanceToBase = (composite.shadowMap.w - position.y) / sunDirection.y;
    float2 baseCrossing = position.xz + sunDirection.xz * max(distanceToBase, 0.0f);
    float2 uv = (baseCrossing - composite.shadowMap.xy) * composite.shadowMap.z + 0.5f;
    float edge = 0.5f - max(abs(uv.x - 0.5f), abs(uv.y - 0.5f));
    if (edge <= 0.0f)
    {
        return 1.0f;
    }

    float transmittance = GetTexture(composite.shadowHandle).SampleLevel(GetSampler(samplerHandle), uv, 0.0f).r;

    return lerp(1.0f, transmittance, saturate(edge * 10.0f));
}
