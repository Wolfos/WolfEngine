// Ported from Sébastien Hillaire's UnrealEngineSkyAtmosphere reference implementation
// (https://github.com/sebh/UnrealEngineSkyAtmosphere), Resources/SkyAtmosphereCommon.hlsl,
// Resources/RenderSkyCommon.hlsl and Resources/RenderSkyRayMarching.hlsl.
// Copyright Epic Games, Inc. MIT licensed; see NOTICE.md in this folder.
//
// The equations are unchanged. What differs from the reference:
// - Globals (LUT textures, sun illuminance, ray-march sample counts, the multiple-scattering LUT resolution)
//   are passed as arguments, and textures are bindless handles.
// - The depth-buffer, shadow-map and GPU-debug paths of IntegrateScatteredLuminance are removed; no caller here
//   uses them.
// - MULTISCATAPPROX_ENABLED is a runtime argument instead of a permutation.
// - The Sky View LUT parameterisation clamps its cosines and its acos and sqrt arguments. fromSubUvsToUnit maps the
//   last texel column a hair past 1, which made lightViewCosAngle -1.0000004 and the Sky View pass's
//   sqrt(1 - cos^2) NaN for every view facing away from the sun; rounding can likewise push a zenith cosine past 1,
//   and near the horizon the caller's ground test can disagree with the angle. The reference's demo never looked
//   there, but the NaN spreads through image-based lighting and volumetric fog here.
//
// Conventions are the reference's: kilometres, +Z up, the planet's center at the origin.

import "../../Common/common_bindless.slang";

static const float HILLAIRE_PI = 3.1415926535897932384626433832795f;
static const float PLANET_RADIUS_OFFSET = 0.01f;

static const float TRANSMITTANCE_TEXTURE_WIDTH = 256.0f;
static const float TRANSMITTANCE_TEXTURE_HEIGHT = 64.0f;
static const float MULTI_SCATTERING_LUT_RES = 32.0f;
static const float SKY_VIEW_LUT_WIDTH = 192.0f;
static const float SKY_VIEW_LUT_HEIGHT = 108.0f;

static const float AP_SLICE_COUNT = 32.0f;
static const float AP_KM_PER_SLICE = 4.0f;

struct AtmosphereParameters
{
    // Radius of the planet (center to ground)
    float BottomRadius;
    // Maximum considered atmosphere height (center to atmosphere top)
    float TopRadius;

    // Rayleigh scattering exponential distribution scale in the atmosphere
    float RayleighDensityExpScale;
    // Rayleigh scattering coefficients
    float3 RayleighScattering;

    // Mie scattering exponential distribution scale in the atmosphere
    float MieDensityExpScale;
    // Mie scattering coefficients
    float3 MieScattering;
    // Mie extinction coefficients
    float3 MieExtinction;
    // Mie absorption coefficients
    float3 MieAbsorption;
    // Mie phase function excentricity
    float MiePhaseG;

    // Another medium type in the atmosphere
    float AbsorptionDensity0LayerWidth;
    float AbsorptionDensity0ConstantTerm;
    float AbsorptionDensity0LinearTerm;
    float AbsorptionDensity1ConstantTerm;
    float AbsorptionDensity1LinearTerm;
    // This other medium only absorb light, e.g. useful to represent ozone in the earth atmosphere
    float3 AbsorptionExtinction;

    // The albedo of the ground.
    float3 GroundAlbedo;
};

/// LUTs and per-frame inputs the reference reads from globals.
struct AtmosphereLuts
{
    uint transmittanceLut;
    uint multiScatteringLut;
    uint linearClampSampler;
};

// - r0: ray origin
// - rd: normalized ray direction
// - s0: sphere center
// - sR: sphere radius
// - Returns distance from r0 to first intersecion with sphere,
//   or -1.0 if no intersection.
float raySphereIntersectNearest(float3 r0, float3 rd, float3 s0, float sR)
{
    float a = dot(rd, rd);
    float3 s0_r0 = r0 - s0;
    float b = 2.0 * dot(rd, s0_r0);
    float c = dot(s0_r0, s0_r0) - (sR * sR);
    float delta = b * b - 4.0 * a * c;
    if (delta < 0.0 || a == 0.0)
    {
        return -1.0;
    }
    float sol0 = (-b - sqrt(delta)) / (2.0 * a);
    float sol1 = (-b + sqrt(delta)) / (2.0 * a);
    if (sol0 < 0.0 && sol1 < 0.0)
    {
        return -1.0;
    }
    if (sol0 < 0.0)
    {
        return max(0.0, sol1);
    }
    else if (sol1 < 0.0)
    {
        return max(0.0, sol0);
    }
    return max(0.0, min(sol0, sol1));
}

void LutTransmittanceParamsToUv(AtmosphereParameters Atmosphere, in float viewHeight, in float viewZenithCosAngle, out float2 uv)
{
    float H = sqrt(max(0.0f, Atmosphere.TopRadius * Atmosphere.TopRadius - Atmosphere.BottomRadius * Atmosphere.BottomRadius));
    float rho = sqrt(max(0.0f, viewHeight * viewHeight - Atmosphere.BottomRadius * Atmosphere.BottomRadius));

    float discriminant = viewHeight * viewHeight * (viewZenithCosAngle * viewZenithCosAngle - 1.0) + Atmosphere.TopRadius * Atmosphere.TopRadius;
    float d = max(0.0, (-viewHeight * viewZenithCosAngle + sqrt(discriminant))); // Distance to atmosphere boundary

    float d_min = Atmosphere.TopRadius - viewHeight;
    float d_max = rho + H;
    float x_mu = (d - d_min) / (d_max - d_min);
    float x_r = rho / H;

    uv = float2(x_mu, x_r);
}

// Transmittance LUT function parameterisation from Bruneton 2017 https://github.com/ebruneton/precomputed_atmospheric_scattering
// uv in [0,1]
// viewZenithCosAngle in [-1,1]
// viewHeight in [bottomRAdius, topRadius]
float fromUnitToSubUvs(float u, float resolution) { return (u + 0.5f / resolution) * (resolution / (resolution + 1.0f)); }
float fromSubUvsToUnit(float u, float resolution) { return (u - 0.5f / resolution) * (resolution / (resolution - 1.0f)); }

void UvToLutTransmittanceParams(AtmosphereParameters Atmosphere, out float viewHeight, out float viewZenithCosAngle, in float2 uv)
{
    float x_mu = uv.x;
    float x_r = uv.y;

    float H = sqrt(Atmosphere.TopRadius * Atmosphere.TopRadius - Atmosphere.BottomRadius * Atmosphere.BottomRadius);
    float rho = H * x_r;
    viewHeight = sqrt(rho * rho + Atmosphere.BottomRadius * Atmosphere.BottomRadius);

    float d_min = Atmosphere.TopRadius - viewHeight;
    float d_max = rho + H;
    float d = d_min + x_mu * (d_max - d_min);
    viewZenithCosAngle = d == 0.0 ? 1.0f : (H * H - rho * rho - d * d) / (2.0 * viewHeight * d);
    viewZenithCosAngle = clamp(viewZenithCosAngle, -1.0, 1.0);
}

void UvToSkyViewLutParams(AtmosphereParameters Atmosphere, out float viewZenithCosAngle, out float lightViewCosAngle, in float viewHeight, in float2 uv)
{
    // Constrain uvs to valid sub texel range (avoid zenith derivative issue making LUT usage visible)
    uv = float2(fromSubUvsToUnit(uv.x, SKY_VIEW_LUT_WIDTH), fromSubUvsToUnit(uv.y, SKY_VIEW_LUT_HEIGHT));

    float Vhorizon = sqrt(viewHeight * viewHeight - Atmosphere.BottomRadius * Atmosphere.BottomRadius);
    float CosBeta = Vhorizon / viewHeight; // GroundToHorizonCos
    float Beta = acos(CosBeta);
    float ZenithHorizonAngle = HILLAIRE_PI - Beta;

    if (uv.y < 0.5f)
    {
        float coord = 2.0 * uv.y;
        coord = 1.0 - coord;
        coord *= coord;
        coord = 1.0 - coord;
        viewZenithCosAngle = cos(ZenithHorizonAngle * coord);
    }
    else
    {
        float coord = uv.y * 2.0 - 1.0;
        coord *= coord;
        viewZenithCosAngle = cos(ZenithHorizonAngle + Beta * coord);
    }

    float coord = saturate(uv.x);
    coord *= coord;
    lightViewCosAngle = clamp(-(coord * 2.0 - 1.0), -1.0f, 1.0f);
}

void SkyViewLutParamsToUv(AtmosphereParameters Atmosphere, in bool IntersectGround, in float viewZenithCosAngle, in float lightViewCosAngle, in float viewHeight, out float2 uv)
{
    float Vhorizon = sqrt(viewHeight * viewHeight - Atmosphere.BottomRadius * Atmosphere.BottomRadius);
    float CosBeta = Vhorizon / viewHeight; // GroundToHorizonCos
    float Beta = acos(CosBeta);
    float ZenithHorizonAngle = HILLAIRE_PI - Beta;

    viewZenithCosAngle = clamp(viewZenithCosAngle, -1.0f, 1.0f);
    if (!IntersectGround)
    {
        float coord = acos(viewZenithCosAngle) / ZenithHorizonAngle;
        coord = 1.0 - coord;
        coord = sqrt(max(coord, 0.0f));
        coord = 1.0 - coord;
        uv.y = coord * 0.5f;
    }
    else
    {
        float coord = (acos(viewZenithCosAngle) - ZenithHorizonAngle) / Beta;
        coord = sqrt(max(coord, 0.0f));
        uv.y = coord * 0.5f + 0.5f;
    }

    {
        float coord = -lightViewCosAngle * 0.5f + 0.5f;
        coord = sqrt(max(coord, 0.0f));
        uv.x = coord;
    }

    // Constrain uvs to valid sub texel range (avoid zenith derivative issue making LUT usage visible)
    uv = float2(fromUnitToSubUvs(uv.x, SKY_VIEW_LUT_WIDTH), fromUnitToSubUvs(uv.y, SKY_VIEW_LUT_HEIGHT));
}

float getAlbedo(float scattering, float extinction)
{
    return scattering / max(0.001, extinction);
}
float3 getAlbedo(float3 scattering, float3 extinction)
{
    return scattering / max(0.001, extinction);
}

struct MediumSampleRGB
{
    float3 scattering;
    float3 absorption;
    float3 extinction;

    float3 scatteringMie;
    float3 absorptionMie;
    float3 extinctionMie;

    float3 scatteringRay;
    float3 absorptionRay;
    float3 extinctionRay;

    float3 scatteringOzo;
    float3 absorptionOzo;
    float3 extinctionOzo;

    float3 albedo;
};

MediumSampleRGB sampleMediumRGB(in float3 WorldPos, in AtmosphereParameters Atmosphere)
{
    const float viewHeight = length(WorldPos) - Atmosphere.BottomRadius;

    const float densityMie = exp(Atmosphere.MieDensityExpScale * viewHeight);
    const float densityRay = exp(Atmosphere.RayleighDensityExpScale * viewHeight);
    const float densityOzo = saturate(viewHeight < Atmosphere.AbsorptionDensity0LayerWidth ?
        Atmosphere.AbsorptionDensity0LinearTerm * viewHeight + Atmosphere.AbsorptionDensity0ConstantTerm :
        Atmosphere.AbsorptionDensity1LinearTerm * viewHeight + Atmosphere.AbsorptionDensity1ConstantTerm);

    MediumSampleRGB s;

    s.scatteringMie = densityMie * Atmosphere.MieScattering;
    s.absorptionMie = densityMie * Atmosphere.MieAbsorption;
    s.extinctionMie = densityMie * Atmosphere.MieExtinction;

    s.scatteringRay = densityRay * Atmosphere.RayleighScattering;
    s.absorptionRay = 0.0f;
    s.extinctionRay = s.scatteringRay + s.absorptionRay;

    s.scatteringOzo = 0.0;
    s.absorptionOzo = densityOzo * Atmosphere.AbsorptionExtinction;
    s.extinctionOzo = s.scatteringOzo + s.absorptionOzo;

    s.scattering = s.scatteringMie + s.scatteringRay + s.scatteringOzo;
    s.absorption = s.absorptionMie + s.absorptionRay + s.absorptionOzo;
    s.extinction = s.extinctionMie + s.extinctionRay + s.extinctionOzo;
    s.albedo = getAlbedo(s.scattering, s.extinction);

    return s;
}

float RayleighPhase(float cosTheta)
{
    float factor = 3.0f / (16.0f * HILLAIRE_PI);
    return factor * (1.0f + cosTheta * cosTheta);
}

float CornetteShanksMiePhaseFunction(float g, float cosTheta)
{
    float k = 3.0 / (8.0 * HILLAIRE_PI) * (1.0 - g * g) / (2.0 + g * g);
    return k * (1.0 + cosTheta * cosTheta) / pow(1.0 + g * g - 2.0 * g * -cosTheta, 1.5);
}

// The reference uses Cornette-Shanks (USE_CornetteShanks).
float hgPhase(float g, float cosTheta)
{
    return CornetteShanksMiePhaseFunction(g, cosTheta);
}

float3 SampleTransmittanceLut(AtmosphereParameters Atmosphere, AtmosphereLuts luts, float viewHeight, float viewZenithCosAngle)
{
    float2 uv;
    LutTransmittanceParamsToUv(Atmosphere, viewHeight, viewZenithCosAngle, uv);
    Texture2D<float4> transmittanceLut = GetTexture(luts.transmittanceLut);
    return transmittanceLut.SampleLevel(GetSampler(luts.linearClampSampler), uv, 0).rgb;
}

float3 GetMultipleScattering(AtmosphereParameters Atmosphere, AtmosphereLuts luts, float3 scattering, float3 extinction, float3 worlPos, float viewZenithCosAngle)
{
    float2 uv = saturate(float2(viewZenithCosAngle * 0.5f + 0.5f, (length(worlPos) - Atmosphere.BottomRadius) / (Atmosphere.TopRadius - Atmosphere.BottomRadius)));
    uv = float2(fromUnitToSubUvs(uv.x, MULTI_SCATTERING_LUT_RES), fromUnitToSubUvs(uv.y, MULTI_SCATTERING_LUT_RES));

    Texture2D<float4> multiScatteringLut = GetTexture(luts.multiScatteringLut);
    float3 multiScatteredLuminance = multiScatteringLut.SampleLevel(GetSampler(luts.linearClampSampler), uv, 0).rgb;
    return multiScatteredLuminance;
}

bool MoveToTopAtmosphere(inout float3 WorldPos, in float3 WorldDir, in float AtmosphereTopRadius)
{
    float viewHeight = length(WorldPos);
    if (viewHeight > AtmosphereTopRadius)
    {
        float tTop = raySphereIntersectNearest(WorldPos, WorldDir, float3(0.0f, 0.0f, 0.0f), AtmosphereTopRadius);
        if (tTop >= 0.0f)
        {
            float3 UpVector = WorldPos / viewHeight;
            float3 UpOffset = UpVector * -PLANET_RADIUS_OFFSET;
            WorldPos = WorldPos + WorldDir * tTop + UpOffset;
        }
        else
        {
            // Ray is not intersecting the atmosphere
            return false;
        }
    }
    return true; // ok to start tracing
}

struct SingleScatteringResult
{
    float3 L;               // Scattered light (luminance)
    float3 OpticalDepth;    // Optical depth (1/m)
    float3 Transmittance;   // Transmittance in [0,1] (unitless)
    float3 MultiScatAs1;

    float3 NewMultiScatStep0Out;
    float3 NewMultiScatStep1Out;
};

/// The reference's IntegrateScatteredLuminance. globalL is the sun illuminance (1 when building a transfer
/// function, as ILLUMINANCE_IS_ONE does). rayMarchMinMaxSpp is the reference's RayMarchMinMaxSPP, used when
/// VariableSampleCount is true. useMultiScatteringLut is MULTISCATAPPROX_ENABLED.
SingleScatteringResult IntegrateScatteredLuminance(
    in float3 WorldPos, in float3 WorldDir, in float3 SunDir, in AtmosphereParameters Atmosphere, in AtmosphereLuts luts,
    in float3 globalL, in bool useMultiScatteringLut, in float2 rayMarchMinMaxSpp,
    in bool ground, in float SampleCountIni, in bool VariableSampleCount,
    in bool MieRayPhase, in float tMaxMax = 9000000.0f)
{
    SingleScatteringResult result = (SingleScatteringResult)0;

    // Compute next intersection with atmosphere or ground
    float3 earthO = float3(0.0f, 0.0f, 0.0f);
    float tBottom = raySphereIntersectNearest(WorldPos, WorldDir, earthO, Atmosphere.BottomRadius);
    float tTop = raySphereIntersectNearest(WorldPos, WorldDir, earthO, Atmosphere.TopRadius);
    float tMax = 0.0f;
    if (tBottom < 0.0f)
    {
        if (tTop < 0.0f)
        {
            tMax = 0.0f; // No intersection with earth nor atmosphere: stop right away
            return result;
        }
        else
        {
            tMax = tTop;
        }
    }
    else
    {
        if (tTop > 0.0f)
        {
            tMax = min(tTop, tBottom);
        }
    }

    tMax = min(tMax, tMaxMax);

    // Sample count
    float SampleCount = SampleCountIni;
    float SampleCountFloor = SampleCountIni;
    float tMaxFloor = tMax;
    if (VariableSampleCount)
    {
        SampleCount = lerp(rayMarchMinMaxSpp.x, rayMarchMinMaxSpp.y, saturate(tMax * 0.01));
        SampleCountFloor = floor(SampleCount);
        tMaxFloor = tMax * SampleCountFloor / SampleCount; // rescale tMax to map to the last entire step segment.
    }
    float dt = tMax / SampleCount;

    // Phase functions
    const float uniformPhase = 1.0 / (4.0 * HILLAIRE_PI);
    const float3 wi = SunDir;
    const float3 wo = WorldDir;
    float cosTheta = dot(wi, wo);
    float MiePhaseValue = hgPhase(Atmosphere.MiePhaseG, -cosTheta); // mnegate cosTheta because due to WorldDir being a "in" direction.
    float RayleighPhaseValue = RayleighPhase(cosTheta);

    // Ray march the atmosphere to integrate optical depth
    float3 L = 0.0f;
    float3 throughput = 1.0;
    float3 OpticalDepth = 0.0;
    float t = 0.0f;
    float tPrev = 0.0;
    const float SampleSegmentT = 0.3f;
    for (float s = 0.0f; s < SampleCount; s += 1.0f)
    {
        if (VariableSampleCount)
        {
            // More expenssive but artefact free
            float t0 = (s) / SampleCountFloor;
            float t1 = (s + 1.0f) / SampleCountFloor;
            // Non linear distribution of sample within the range.
            t0 = t0 * t0;
            t1 = t1 * t1;
            // Make t0 and t1 world space distances.
            t0 = tMaxFloor * t0;
            if (t1 > 1.0)
            {
                t1 = tMax;
            }
            else
            {
                t1 = tMaxFloor * t1;
            }
            t = t0 + (t1 - t0) * SampleSegmentT;
            dt = t1 - t0;
        }
        else
        {
            // Exact difference, important for accuracy of multiple scattering
            float NewT = tMax * (s + SampleSegmentT) / SampleCount;
            dt = NewT - t;
            t = NewT;
        }
        float3 P = WorldPos + t * WorldDir;

        MediumSampleRGB medium = sampleMediumRGB(P, Atmosphere);
        const float3 SampleOpticalDepth = medium.extinction * dt;
        const float3 SampleTransmittance = exp(-SampleOpticalDepth);
        OpticalDepth += SampleOpticalDepth;

        float pHeight = length(P);
        const float3 UpVector = P / pHeight;
        float SunZenithCosAngle = dot(SunDir, UpVector);
        float3 TransmittanceToSun = SampleTransmittanceLut(Atmosphere, luts, pHeight, SunZenithCosAngle);

        float3 PhaseTimesScattering;
        if (MieRayPhase)
        {
            PhaseTimesScattering = medium.scatteringMie * MiePhaseValue + medium.scatteringRay * RayleighPhaseValue;
        }
        else
        {
            PhaseTimesScattering = medium.scattering * uniformPhase;
        }

        // Earth shadow
        float tEarth = raySphereIntersectNearest(P, SunDir, earthO + PLANET_RADIUS_OFFSET * UpVector, Atmosphere.BottomRadius);
        float earthShadow = tEarth >= 0.0f ? 0.0f : 1.0f;

        // Dual scattering for multi scattering
        float3 multiScatteredLuminance = 0.0f;
        if (useMultiScatteringLut)
        {
            multiScatteredLuminance = GetMultipleScattering(Atmosphere, luts, medium.scattering, medium.extinction, P, SunZenithCosAngle);
        }

        float shadow = 1.0f;

        float3 S = globalL * (earthShadow * shadow * TransmittanceToSun * PhaseTimesScattering + multiScatteredLuminance * medium.scattering);

        // MULTI_SCATTERING_POWER_SERIE 1
        float3 MS = medium.scattering * 1;
        float3 MSint = (MS - MS * SampleTransmittance) / medium.extinction;
        result.MultiScatAs1 += throughput * MSint;

        // Evaluate input to multi scattering
        {
            float3 newMS;

            newMS = earthShadow * TransmittanceToSun * medium.scattering * uniformPhase * 1;
            result.NewMultiScatStep0Out += throughput * (newMS - newMS * SampleTransmittance) / medium.extinction;

            newMS = medium.scattering * uniformPhase * multiScatteredLuminance;
            result.NewMultiScatStep1Out += throughput * (newMS - newMS * SampleTransmittance) / medium.extinction;
        }

        // See slide 28 at http://www.frostbite.com/2015/08/physically-based-unified-volumetric-rendering-in-frostbite/
        float3 Sint = (S - S * SampleTransmittance) / medium.extinction; // integrate along the current step segment
        L += throughput * Sint; // accumulate and also take into account the transmittance from previous steps
        throughput *= SampleTransmittance;

        tPrev = t;
    }

    if (ground && tMax == tBottom && tBottom > 0.0)
    {
        // Account for bounced light off the earth
        float3 P = WorldPos + tBottom * WorldDir;
        float pHeight = length(P);

        const float3 UpVector = P / pHeight;
        float SunZenithCosAngle = dot(SunDir, UpVector);
        float3 TransmittanceToSun = SampleTransmittanceLut(Atmosphere, luts, pHeight, SunZenithCosAngle);

        const float NdotL = saturate(dot(normalize(UpVector), normalize(SunDir)));
        L += globalL * TransmittanceToSun * throughput * NdotL * Atmosphere.GroundAlbedo / HILLAIRE_PI;
    }

    result.L = L;
    result.OpticalDepth = OpticalDepth;
    result.Transmittance = throughput;
    return result;
}

float AerialPerspectiveDepthToSlice(float depth)
{
    return depth * (1.0f / AP_KM_PER_SLICE);
}

float AerialPerspectiveSliceToDepth(float slice)
{
    return slice * AP_KM_PER_SLICE;
}
