import "../Common/common_bindless.slang";
import "../Common/common_lighting.slang";
import "../Atmosphere/atmosphere_common.slang";

/// One procedural cumulus layer, filled from CloudFrame on the CPU. Float4 only, so the layout is the same on
/// D3D12 and Metal. Positions are camera-relative world space in metres (+Y up); the planet's centre sits directly
/// below the camera, as in the atmosphere shaders.
struct CloudConstants
{
    /// x: camera altitude above the ground (m). y: cloud base altitude (m). z: cloud top altitude (m).
    /// w: camera distance from the planet's centre (m).
    float4 layer;
    /// x: coverage (0..1). y: extinction at full density (1/m). z: detail erosion strength. w: multiple-scattering strength.
    float4 shape;
    /// x: 1 / weather tile size. y: 1 / shape tile size. z: 1 / detail tile size (all 1/m).
    /// w: how far cloud tops lean downwind of their bases (m).
    float4 noiseScale;
    /// xy: weather map offset (m, world XZ). zw: shape noise offset (m, world XZ).
    float4 noiseOffset0;
    /// xy: detail noise offset (m, world XZ). zw: wind direction (unit, world XZ).
    float4 noiseOffset1;
    /// x: ambient strength. y: forward phase g. z: backward phase g. w: forward lobe weight.
    float4 lighting;
    /// x: primary sample count. y: light sample count. z: maximum trace distance (m). w: unused.
    float4 sampling;
    /// xyz: direction toward the sun (world, +Y up).
    float4 sunDirection;
    /// rgb: the sun light's color times intensity at the top of the atmosphere.
    float4 sunIlluminance;
};

/// Albedo of cloud droplets: almost all extinction is scattering.
static const float CLOUD_SCATTERING_ALBEDO = 0.99f;

/// Opacity at which a march stops: what remains behind is invisible.
static const float CLOUD_MIN_TRANSMITTANCE = 0.01f;

/// Octaves of the multiple-scattering approximation (Wrenninge et al. 2013, as used by Hillaire 2016).
static const uint CLOUD_SCATTERING_OCTAVES = 3u;

/// Thickness of the flat cloud bases, as a fraction of the layer.
static const float CLOUD_BASE_FADE = 0.07f;

/// The noise textures and their sampler.
struct CloudTextures
{
    uint weather;
    uint shape;
    uint detail;
    uint wrapSampler;
};

/// One sample samplePoint of the layer.
struct CloudSamplePoint
{
    float3 position;
    float altitude;
    float heightFraction;
};

float CloudRemap(float value, float oldMin, float oldMax, float newMin, float newMax)
{
    return newMin + (value - oldMin) / max(oldMax - oldMin, 1e-5f) * (newMax - newMin);
}

float CloudBaseAltitude(CloudConstants c)
{
    return c.layer.y;
}

float CloudTopAltitude(CloudConstants c)
{
    return c.layer.z;
}

float CloudCameraAltitude(CloudConstants c)
{
    return c.layer.x;
}

float CloudGroundRadius(CloudConstants c)
{
    return c.layer.w - c.layer.x;
}

/// Altitude above the ground of a camera-relative position. Written so the planet's radius never cancels against
/// itself: the naive length(position + centre) - radius loses the result in float precision.
float CloudAltitude(CloudConstants c, float3 position)
{
    float cameraRadius = c.layer.w;
    float q = 2.0f * cameraRadius * position.y + dot(position, position);

    return CloudCameraAltitude(c) + q / (sqrt(max(cameraRadius * cameraRadius + q, 0.0f)) + cameraRadius);
}

CloudSamplePoint MakeCloudSamplePoint(CloudConstants c, float3 position)
{
    CloudSamplePoint samplePoint;
    samplePoint.position = position;
    samplePoint.altitude = CloudAltitude(c, position);
    samplePoint.heightFraction = saturate((samplePoint.altitude - CloudBaseAltitude(c)) /
        max(CloudTopAltitude(c) - CloudBaseAltitude(c), 1.0f));

    return samplePoint;
}

/// The two distances at which a ray from the camera crosses the sphere at <paramref name="altitude"/>, or
/// (-1, -1) when it misses. The constant term is factored as (camera - altitude)(camera radius + sphere radius),
/// which keeps it exact at planetary radii.
float2 CloudSphereIntersect(CloudConstants c, float3 direction, float altitude)
{
    float cameraRadius = c.layer.w;
    float sphereRadius = CloudGroundRadius(c) + altitude;
    float b = cameraRadius * direction.y;
    float constant = (CloudCameraAltitude(c) - altitude) * (cameraRadius + sphereRadius);
    float discriminant = b * b - constant;
    if (discriminant < 0.0f)
    {
        return float2(-1.0f, -1.0f);
    }

    float root = sqrt(discriminant);

    return float2(-b - root, -b + root);
}

/// The distances along a camera ray over which it is inside the cloud layer, clamped to maxDistance. x >= y when
/// the ray never enters it. The camera can be below, inside or above the layer.
float2 CloudLayerIntersect(CloudConstants c, float3 direction, float maxDistance)
{
    float cameraAltitude = CloudCameraAltitude(c);
    float2 inner = CloudSphereIntersect(c, direction, CloudBaseAltitude(c));
    float2 outer = CloudSphereIntersect(c, direction, CloudTopAltitude(c));
    float2 ground = CloudSphereIntersect(c, direction, 0.0f);
    bool hitsGround = ground.x > 0.0f;

    float start = 0.0f;
    float end = 0.0f;
    if (cameraAltitude < CloudBaseAltitude(c))
    {
        if (hitsGround)
        {
            return float2(1.0f, 0.0f);
        }

        start = inner.y;
        end = outer.y;
    }
    else if (cameraAltitude <= CloudTopAltitude(c))
    {
        start = 0.0f;
        end = inner.x > 0.0f
            ? inner.x
            : outer.y;
    }
    else
    {
        if (outer.x < 0.0f)
        {
            return float2(1.0f, 0.0f);
        }

        start = outer.x;
        end = inner.x > 0.0f
            ? inner.x
            : outer.y;
    }

    start = max(start, 0.0f);
    end = min(end, maxDistance);

    return float2(start, end);
}

/// Nubis-style vertical profile: a sharp, flat base and a rounded top whose height depends on the cloud type,
/// from low, flat clouds (0) to towering cumulus (1).
float CloudHeightGradient(float heightFraction, float cloudType)
{
    float topHeight = lerp(0.45f, 1.0f, cloudType);
    float bottom = smoothstep(0.0f, CLOUD_BASE_FADE, heightFraction);
    float top = 1.0f - smoothstep(topHeight * 0.4f, topHeight, heightFraction);

    return bottom * top;
}

/// Cloud extinction (1/m) at a sample samplePoint. Cheap samples skip the detail erosion, which is what empty-space
/// skipping and distant light samples use.
float SampleCloudDensity(CloudConstants c, CloudTextures textures, CloudSamplePoint samplePoint, bool cheap)
{
    if (samplePoint.altitude < CloudBaseAltitude(c) || samplePoint.altitude > CloudTopAltitude(c))
    {
        return 0.0f;
    }

    SamplerState wrapSampler = GetSampler(textures.wrapSampler);
    float2 windDirection = c.noiseOffset1.zw;
    // Tops lean downwind of their bases.
    float2 skew = windDirection * (samplePoint.heightFraction * c.noiseScale.w);
    float2 horizontal = samplePoint.position.xz - skew;

    float4 weather = GetTexture(textures.weather).SampleLevel(
        wrapSampler,
        (horizontal + c.noiseOffset0.xy) * c.noiseScale.x,
        0.0f);
    float coverage = c.shape.x;
    float localCoverage = saturate(CloudRemap(weather.r, 1.0f - coverage, 1.3f - coverage, 0.0f, 1.0f)) *
        saturate(coverage * 4.0f);
    if (localCoverage <= 0.0f)
    {
        return 0.0f;
    }

    float gradient = CloudHeightGradient(samplePoint.heightFraction, weather.g);
    if (gradient <= 0.0f)
    {
        return 0.0f;
    }

    float3 shapeCoordinate = float3(
        horizontal.x + c.noiseOffset0.z,
        samplePoint.altitude,
        horizontal.y + c.noiseOffset0.w) * c.noiseScale.y;
    float4 shape = GetTexture3D(textures.shape).SampleLevel(wrapSampler, shapeCoordinate, 0.0f);
    float lowFrequencyFbm = shape.g * 0.625f + shape.b * 0.25f + shape.a * 0.125f;
    float baseCloud = saturate(CloudRemap(shape.r, lowFrequencyFbm - 1.0f, 1.0f, 0.0f, 1.0f));
    baseCloud *= gradient;

    float cloud = saturate(CloudRemap(baseCloud, 1.0f - localCoverage, 1.0f, 0.0f, 1.0f)) * localCoverage;
    if (cloud <= 0.0f)
    {
        return 0.0f;
    }

    if (cheap == false && c.shape.z > 0.0f)
    {
        float3 detailCoordinate = float3(
            horizontal.x + c.noiseOffset1.x,
            samplePoint.altitude,
            horizontal.y + c.noiseOffset1.y) * c.noiseScale.z;
        float4 detail = GetTexture3D(textures.detail).SampleLevel(wrapSampler, detailCoordinate, 0.0f);
        float highFrequencyFbm = detail.r * 0.625f + detail.g * 0.25f + detail.b * 0.125f;

        // Wispy at the base, billowing above it.
        float modifier = lerp(highFrequencyFbm, 1.0f - highFrequencyFbm, saturate(samplePoint.heightFraction * 8.0f));
        cloud = saturate(CloudRemap(cloud, modifier * c.shape.z * 0.5f, 1.0f, 0.0f, 1.0f));
    }

    // Weather blue varies density between clouds, and clouds thicken with height as their droplets grow.
    float densityVariation = lerp(0.6f, 1.1f, weather.b) * lerp(0.75f, 1.0f, saturate(samplePoint.heightFraction * 2.0f));

    return cloud * densityVariation * c.shape.y;
}

float CloudHenyeyGreenstein(float cosine, float g)
{
    float g2 = g * g;
    float denominator = max(1.0f + g2 - 2.0f * g * cosine, 1e-4f);

    return (1.0f - g2) / (4.0f * HILLAIRE_PI * denominator * sqrt(denominator));
}

/// Dual-lobe phase: a strong forward peak gives silver linings toward the sun, a weak backward lobe keeps the
/// sides facing away from it lit. Each scattering octave flattens both lobes.
float CloudPhase(CloudConstants c, float cosine, float eccentricityScale)
{
    float forward = CloudHenyeyGreenstein(cosine, c.lighting.y * eccentricityScale);
    float backward = CloudHenyeyGreenstein(cosine, c.lighting.z * eccentricityScale);

    return lerp(backward, forward, c.lighting.w);
}

/// Sunlight reaching a samplePoint at the top of the clouds' path to it, after the atmosphere: the transmittance LUT
/// tints it at low sun and zeroes it inside the planet's shadow, so high clouds stay lit after the ground sunset.
float3 CloudSunAtPoint(CloudConstants c, AtmosphereConstants atmosphereConstants, AtmosphereLuts luts, CloudSamplePoint samplePoint)
{
    float groundRadius = CloudGroundRadius(c);
    float radius = groundRadius + samplePoint.altitude;
    float3 up = float3(samplePoint.position.x, c.layer.w + samplePoint.position.y, samplePoint.position.z) / max(radius, 1.0f);
    float mu = dot(up, c.sunDirection.xyz);
    float horizon = -sqrt(max(1.0f - (groundRadius / radius) * (groundRadius / radius), 0.0f));
    if (mu < horizon)
    {
        return 0.0f;
    }

    AtmosphereParameters atmosphere = GetAtmosphereParameters(atmosphereConstants);
    float3 transmittance = SampleTransmittanceLut(atmosphere, luts, radius * 0.001f, mu);

    return c.sunIlluminance.rgb * transmittance;
}

/// Optical depth toward the sun from a samplePoint, through steps that grow with distance. The last samples skip the
/// detail noise: at their distance it is too fine to shadow anything.
float CloudLightOpticalDepth(CloudConstants c, CloudTextures textures, float3 position, uint sampleCount)
{
    float layerThickness = CloudTopAltitude(c) - CloudBaseAltitude(c);
    float reach = min(layerThickness, 2500.0f);
    float stepUnit = reach / max(float(sampleCount * (sampleCount + 1u)) * 0.5f, 1.0f);
    float opticalDepth = 0.0f;
    float distance = 0.0f;
    for (uint i = 0u; i < sampleCount; i++)
    {
        float stepLength = stepUnit * float(i + 1u);
        float3 samplePosition = position + c.sunDirection.xyz * (distance + stepLength * 0.5f);
        CloudSamplePoint samplePoint = MakeCloudSamplePoint(c, samplePosition);
        bool cheap = i >= 2u;
        opticalDepth += SampleCloudDensity(c, textures, samplePoint, cheap) * stepLength;
        distance += stepLength;
    }

    return opticalDepth;
}

/// In-scattered radiance per unit extinction at a sample: direct sun through the cloud above it, scattered
/// several times by the octave approximation, plus skylight.
float3 CloudScatteredRadiance(
    CloudConstants c,
    float3 sunAtPoint,
    float lightOpticalDepth,
    float phaseCosine,
    float heightFraction,
    float3 ambientTop,
    float3 ambientBottom)
{
    float3 sun = 0.0f;
    float scatteringWeight = 1.0f;
    float extinctionWeight = 1.0f;
    float eccentricityScale = 1.0f;
    float multipleScattering = c.shape.w;
    for (uint octave = 0u; octave < CLOUD_SCATTERING_OCTAVES; octave++)
    {
        float transmittance = exp(-lightOpticalDepth * extinctionWeight);
        sun += sunAtPoint * (scatteringWeight * transmittance * CloudPhase(c, phaseCosine, eccentricityScale));
        scatteringWeight *= multipleScattering * 0.8f;
        extinctionWeight *= 0.5f;
        eccentricityScale *= 0.5f;
    }

    // Skylight falls on the tops; the bases see the darker ground and the cloud above them.
    float3 ambient = lerp(ambientBottom, ambientTop, saturate(0.25f + heightFraction)) * c.lighting.x;

    return CLOUD_SCATTERING_ALBEDO * (sun + ambient);
}

/// Front-to-back integration of one ray through the layer.
struct CloudMarchResult
{
    float3 radiance;
    float transmittance;
    /// Opacity-weighted mean distance of the cloud along the ray (m), or the fallback when it saw no cloud.
    float depth;
};

/// Marches [range.x, range.y] along direction. Empty space is crossed in long cheap steps; on a hit the march
/// steps back and continues in fine steps with full detail, returning to long steps after a run of empty samples.
CloudMarchResult MarchClouds(
    CloudConstants c,
    AtmosphereConstants atmosphereConstants,
    AtmosphereLuts luts,
    CloudTextures textures,
    float3 direction,
    float2 range,
    float jitter,
    uint primarySamples,
    uint lightSamples,
    float3 ambientTop,
    float3 ambientBottom)
{
    CloudMarchResult result;
    result.radiance = 0.0f;
    result.transmittance = 1.0f;
    result.depth = range.x < range.y
        ? 0.5f * (range.x + range.y)
        : c.sampling.z;
    if (range.x >= range.y)
    {
        return result;
    }

    float rayLength = range.y - range.x;
    float layerThickness = CloudTopAltitude(c) - CloudBaseAltitude(c);

    // Short rays through the layer's depth take every sample; long grazing rays spread them out.
    float stepLength = max(rayLength / float(max(primarySamples, 1u)), layerThickness / float(max(primarySamples, 1u)) * 0.5f);
    float coarseStep = stepLength * 2.0f;
    float phaseCosine = dot(direction, c.sunDirection.xyz);

    float t = range.x + coarseStep * jitter;
    bool fine = false;
    uint emptyRun = 0u;
    float depthWeight = 0.0f;
    float weightedDepth = 0.0f;
    uint maxIterations = primarySamples * 2u + 8u;
    for (uint iteration = 0u; iteration < maxIterations; iteration++)
    {
        if (t >= range.y)
        {
            break;
        }

        float3 position = direction * t;
        CloudSamplePoint samplePoint = MakeCloudSamplePoint(c, position);
        if (fine == false)
        {
            float probe = SampleCloudDensity(c, textures, samplePoint, true);
            if (probe <= 0.0f)
            {
                t += coarseStep;
                continue;
            }

            // Step back so the fine march starts at the edge rather than inside the cloud.
            fine = true;
            emptyRun = 0u;
            t = max(t - coarseStep * 0.5f, range.x);
            continue;
        }

        float extinction = SampleCloudDensity(c, textures, samplePoint, false);
        float dt = min(stepLength, range.y - t);
        if (extinction > 0.0f)
        {
            emptyRun = 0u;
            float opticalDepth = CloudLightOpticalDepth(c, textures, position, lightSamples);
            float3 sunAtPoint = CloudSunAtPoint(c, atmosphereConstants, luts, samplePoint);
            float3 source = extinction * CloudScatteredRadiance(
                c,
                sunAtPoint,
                opticalDepth,
                phaseCosine,
                samplePoint.heightFraction,
                ambientTop,
                ambientBottom);

            // Energy-conserving integration over the step (Hillaire 2015).
            float stepTransmittance = exp(-extinction * dt);
            float3 integrated = (source - source * stepTransmittance) / extinction;
            result.radiance += result.transmittance * integrated;

            float opacity = result.transmittance * (1.0f - stepTransmittance);
            depthWeight += opacity;
            weightedDepth += opacity * (t + dt * 0.5f);

            result.transmittance *= stepTransmittance;
            if (result.transmittance < CLOUD_MIN_TRANSMITTANCE)
            {
                break;
            }
        }
        else
        {
            emptyRun++;
            if (emptyRun >= 6u)
            {
                fine = false;
            }
        }

        t += dt;
    }

    if (depthWeight > 1e-4f)
    {
        result.depth = weightedDepth / depthWeight;
    }

    // Treat what remains as fully transmitted rather than leaving a visible floor of 1%.
    if (result.transmittance < CLOUD_MIN_TRANSMITTANCE)
    {
        result.transmittance = 0.0f;
    }

    return result;
}

/// Skylight above and below the clouds, from the view's irradiance environment.
void SampleCloudAmbient(uint irradianceHandle, uint samplerHandle, out float3 ambientTop, out float3 ambientBottom)
{
    ambientTop = SampleEnvironment(irradianceHandle, samplerHandle, float3(0.0f, 1.0f, 0.0f)) / HILLAIRE_PI;
    ambientBottom = SampleEnvironment(irradianceHandle, samplerHandle, float3(0.0f, -1.0f, 0.0f)) / HILLAIRE_PI;
}
