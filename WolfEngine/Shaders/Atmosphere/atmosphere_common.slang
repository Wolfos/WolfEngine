import "../Common/common_bindless.slang";
__exported import "../ThirdParty/Hillaire/sky_atmosphere.slang";

/// The Procedural sky's atmosphere, filled from AtmosphereModel on the CPU. Float4 only, so the layout is the same
/// on D3D12 and Metal. Directions are in the atmosphere's frame (+Z up); see ToAtmosphereDirection.
struct AtmosphereConstants
{
    /// xyz: Rayleigh scattering (1/km). w: Rayleigh density exponential scale.
    float4 rayleighScattering;
    /// xyz: Mie scattering (1/km). w: Mie density exponential scale.
    float4 mieScattering;
    /// xyz: Mie extinction (1/km). w: Mie phase g.
    float4 mieExtinction;
    /// xyz: Mie absorption (1/km). w: ground radius (km).
    float4 mieAbsorption;
    /// xyz: ozone absorption (1/km). w: atmosphere top radius (km).
    float4 absorptionExtinction;
    /// xyz: ground albedo.
    float4 groundAlbedo;
    /// x: layer 0 width (km), y: layer 0 constant term, z: layer 0 linear term, w: layer 1 constant term.
    float4 absorptionDensity0;
    /// x: layer 1 linear term.
    float4 absorptionDensity1;
    /// x: camera distance from the planet's center (km). y: sun disc half angle (radians).
    float4 view;
    /// xyz: direction toward the sun.
    float4 sunDirection;
    /// rgb: the sun light's color times intensity.
    float4 sunIlluminance;
};

/// Sample counts of the reference's view ray march (its RayMarchMinMaxSPP defaults).
static const float2 ATMOSPHERE_VIEW_RAY_MARCH_MIN_MAX_SPP = float2(4.0f, 14.0f);

/// Largest sun disc luminance written to the 16-bit float lighting target, which overflows at 65504.
static const float ATMOSPHERE_MAX_SUN_DISC_LUMINANCE = 32768.0f;

AtmosphereParameters GetAtmosphereParameters(AtmosphereConstants c)
{
    AtmosphereParameters parameters;
    parameters.BottomRadius = c.mieAbsorption.w;
    parameters.TopRadius = c.absorptionExtinction.w;
    parameters.RayleighDensityExpScale = c.rayleighScattering.w;
    parameters.RayleighScattering = c.rayleighScattering.xyz;
    parameters.MieDensityExpScale = c.mieScattering.w;
    parameters.MieScattering = c.mieScattering.xyz;
    parameters.MieExtinction = c.mieExtinction.xyz;
    parameters.MieAbsorption = c.mieAbsorption.xyz;
    parameters.MiePhaseG = c.mieExtinction.w;
    parameters.AbsorptionDensity0LayerWidth = c.absorptionDensity0.x;
    parameters.AbsorptionDensity0ConstantTerm = c.absorptionDensity0.y;
    parameters.AbsorptionDensity0LinearTerm = c.absorptionDensity0.z;
    parameters.AbsorptionDensity1ConstantTerm = c.absorptionDensity0.w;
    parameters.AbsorptionDensity1LinearTerm = c.absorptionDensity1.x;
    parameters.AbsorptionExtinction = c.absorptionExtinction.xyz;
    parameters.GroundAlbedo = c.groundAlbedo.xyz;

    return parameters;
}

AtmosphereLuts MakeAtmosphereLuts(uint transmittanceLut, uint multiScatteringLut, uint linearClampSampler)
{
    AtmosphereLuts luts;
    luts.transmittanceLut = transmittanceLut;
    luts.multiScatteringLut = multiScatteringLut;
    luts.linearClampSampler = linearClampSampler;

    return luts;
}

/// A world direction (+Y up) in the atmosphere's frame (+Z up). The model is symmetric, so swapping the axes'
/// handedness changes nothing it computes.
float3 ToAtmosphereDirection(float3 worldDirection)
{
    return float3(worldDirection.x, worldDirection.z, worldDirection.y);
}

/// The camera in the atmosphere's frame. The planet sits directly below the camera, so only altitude matters.
float3 GetAtmosphereCameraPosition(AtmosphereConstants c)
{
    return float3(0.0f, 0.0f, c.view.x);
}

/// The reference's fast-sky lookup: the Sky View LUT's radiance along a world direction, including the ground
/// below the horizon.
float3 SampleSkyViewLut(AtmosphereConstants c, uint skyViewLut, uint linearClampSampler, float3 worldDirection)
{
    AtmosphereParameters atmosphere = GetAtmosphereParameters(c);
    float3 worldPos = GetAtmosphereCameraPosition(c);
    float3 worldDir = normalize(ToAtmosphereDirection(worldDirection));
    float viewHeight = length(worldPos);

    float3 upVector = normalize(worldPos);
    float viewZenithCosAngle = dot(worldDir, upVector);

    // The reference assumes the view is never exactly vertical; there the sun's azimuth has no meaning.
    float3 side = cross(upVector, worldDir);
    float lightViewCosAngle = 1.0f;
    if (dot(side, side) > 1e-10f)
    {
        float3 sideVector = normalize(side);
        float3 forwardVector = normalize(cross(sideVector, upVector));
        float2 lightOnPlane = float2(dot(c.sunDirection.xyz, forwardVector), dot(c.sunDirection.xyz, sideVector));
        if (dot(lightOnPlane, lightOnPlane) > 1e-10f)
        {
            lightOnPlane = normalize(lightOnPlane);
            lightViewCosAngle = lightOnPlane.x;
        }
    }

    bool intersectGround = raySphereIntersectNearest(worldPos, worldDir, float3(0.0f, 0.0f, 0.0f), atmosphere.BottomRadius) >= 0.0f;

    float2 uv;
    SkyViewLutParamsToUv(atmosphere, intersectGround, viewZenithCosAngle, lightViewCosAngle, viewHeight, uv);

    return GetTexture(skyViewLut).SampleLevel(GetSampler(linearClampSampler), uv, 0.0f).rgb;
}

/// The sun's disc seen along a world direction: the sun's illuminance spread over the disc's solid angle, dimmed by
/// the atmosphere toward the viewer and limb-darkened. Zero off the disc and behind the planet.
float3 GetSunDiscLuminance(AtmosphereConstants c, AtmosphereLuts luts, float3 worldDirection)
{
    float halfAngle = c.view.y;
    if (halfAngle <= 0.0f)
    {
        return 0.0f;
    }

    float3 worldDir = normalize(ToAtmosphereDirection(worldDirection));
    float3 sunDir = c.sunDirection.xyz;
    if (dot(worldDir, sunDir) <= 0.0f)
    {
        return 0.0f;
    }

    // asin of the cross product keeps precision at the disc's tiny angles, where acos of the dot product does not.
    float angle = asin(min(length(cross(worldDir, sunDir)), 1.0f));
    if (angle >= halfAngle)
    {
        return 0.0f;
    }

    AtmosphereParameters atmosphere = GetAtmosphereParameters(c);
    float3 worldPos = GetAtmosphereCameraPosition(c);
    if (raySphereIntersectNearest(worldPos, worldDir, float3(0.0f, 0.0f, 0.0f), atmosphere.BottomRadius) >= 0.0f)
    {
        return 0.0f;
    }

    float viewHeight = length(worldPos);
    float3 transmittance = SampleTransmittanceLut(atmosphere, luts, viewHeight, dot(worldDir, worldPos / viewHeight));

    // Limb darkening, as in Unreal's SkyAtmosphere, coefficients for 680, 550 and 440 nm.
    float centerToEdge = angle / halfAngle;
    float mu = sqrt(max(1.0f - centerToEdge * centerToEdge, 0.0f));
    float3 limbDarkening = pow(mu, float3(0.397f, 0.503f, 0.652f));

    float solidAngle = 2.0f * HILLAIRE_PI * (1.0f - cos(halfAngle));
    float3 luminance = c.sunIlluminance.rgb / solidAngle * transmittance * limbDarkening;

    return min(luminance, ATMOSPHERE_MAX_SUN_DISC_LUMINANCE);
}

/// The reference's fast aerial perspective lookup at a screen position and distance from the camera. rgb is the
/// in-scattered light and a the opacity, so a surface's radiance becomes color * (1 - a) + rgb.
float4 SampleAerialPerspective(uint aerialPerspectiveVolume, uint linearClampSampler, float2 screenUv, float distanceMetres)
{
    float tDepth = distanceMetres * 0.001f;
    float slice = AerialPerspectiveDepthToSlice(tDepth);
    float weight = 1.0f;
    if (slice < 0.5f)
    {
        // We multiply by weight to fade to 0 at depth 0. That works for luminance and opacity.
        weight = saturate(slice * 2.0f);
        slice = 0.5f;
    }
    float w = sqrt(slice / AP_SLICE_COUNT); // squared distribution

    Texture3D<float4> volume = GetTexture3D(aerialPerspectiveVolume);

    return weight * volume.SampleLevel(GetSampler(linearClampSampler), float3(screenUv, w), 0.0f);
}

float3 ApplyAerialPerspective(float3 color, float4 aerialPerspective)
{
    return color * (1.0f - aerialPerspective.a) + aerialPerspective.rgb;
}
