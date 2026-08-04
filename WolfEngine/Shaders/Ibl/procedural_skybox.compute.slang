// Copyright (c) 2024 Felix Westin
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
// Adapted for WolfEngine from Felix Westin's Fast Atmosphere shader. The
// atmosphere model is preserved while the final look keeps the engine's
// existing ground, horizon, and zenith color controls as artistic overrides.

import "ibl_common.slang";
import "../Common/common_bindless.slang";

cbuffer BindlessHandles : register(b0)
{
    uint environmentHandle;
    uint width;
    uint height;
    uint _padding;
};

cbuffer SkyParams : register(b1)
{
    float4 sunDirectionIntensity; // xyz dir, w intensity
    float4 sunColorSharpness; // xyz color, w sun disc sharpness proxy
    float4 skyTop; // xyz zenith tint, w zenith falloff
    float4 skyHorizon; // xyz horizon tint, w ground blend
    float4 ground; // xyz ground tint, w unused
};

static const float kInfinity = 3.402823466e38f;

static const float kAtmosphereHeight = 100000.0f;
static const float kAtmosphereDensity = 1.0f;
static const float kPlanetRadius = 6371000.0f;
static const float3 kPlanetCenter = float3(0.0f, -kPlanetRadius, 0.0f);
static const float3 kRayleighCoeff = float3(5.802f, 13.558f, 33.100f) * 1e-6f;
static const float3 kMieCoeff = float3(3.996f, 3.996f, 3.996f) * 1e-6f;
static const float3 kOzoneCoeff = float3(0.650f, 1.881f, 0.085f) * 1e-6f;

static const float kRayleighMaxLum = 2.5f;
static const float kMieMaxLum = 0.5f;

static const float kExposureMul = 0.23f;
static const float kFakeMultiScatter = 0.3f;
static const float kAerialScale = 3.0f;
static const float kAerialMul = 2.5f;
static const float kTransmittanceMul = 0.25f;
static const float kLightTransmittanceScale = 1e6f;
static const float kDensityHeightMod = 1e-12f;
static const float kDensityCamMod = 10.0f;
static const float kOzoneMul = 1.5f;
static const float kOzoneLightMul = 5.0f;
static const float3 kMieTint = float3(0.95f, 0.85f, 0.75f);
static const float kNightLight = 2e-3f;
static const float kInvPi = 0.31830988618f;

static const float kObserverHeight = 1800.0f;
static const float3 kLuminanceWeights = float3(0.2126f, 0.7152f, 0.0722f);

static float Sq(float x)
{
    return x * x;
}

static float Pow4(float x)
{
    float xx = x * x;
    return xx * xx;
}

static float Pow8(float x)
{
    float xxxx = Pow4(x);
    return xxxx * xxxx;
}

static float Hash13(float3 p)
{
    return frac(sin(dot(p, float3(127.1f, 311.7f, 74.7f))) * 43758.5453123f);
}

static float2 Hash23(float3 p)
{
    return frac(sin(float2(
        dot(p, float3(127.1f, 311.7f, 74.7f)),
        dot(p, float3(269.5f, 183.3f, 246.1f)))) * 43758.5453123f);
}

static float2 SphereIntersection(float3 rayStart, float3 rayDir, float3 sphereCenter, float sphereRadius)
{
    float3 oc = rayStart - sphereCenter;
    float b = dot(oc, rayDir);
    float c = dot(oc, oc) - Sq(sphereRadius);
    float h = Sq(b) - c;
    if (h < 0.0f)
    {
        return float2(-1.0f, -1.0f);
    }

    float root = sqrt(h);
    return float2(-b - root, -b + root);
}

static float2 PlanetIntersection(float3 rayStart, float3 rayDir)
{
    return SphereIntersection(rayStart, rayDir, kPlanetCenter, kPlanetRadius);
}

static float2 AtmosphereIntersection(float3 rayStart, float3 rayDir)
{
    return SphereIntersection(rayStart, rayDir, kPlanetCenter, kPlanetRadius + kAtmosphereHeight);
}

static float PhaseR(float cosTheta)
{
    return (1.0f + Sq(cosTheta)) * 0.06f;
}

static float PhaseM(float cosTheta, float g)
{
    g = min(g, 0.9381f);
    float k = 1.55f * g - 0.55f * Sq(g) * g;
    float a = 1.0f - Sq(k);
    float b = 12.57f * Sq(1.0f - k * cosTheta);
    return a / b;
}

static float3 GetLightTransmittance(float3 lightDir, float multiplier, float ozoneMultiplier)
{
    float lightExtinctionAmount =
        exp(-(saturate(lightDir.y + 0.05f) * 40.0f)) +
        exp(-(saturate(lightDir.y + 0.5f) * 5.0f)) * 0.4f +
        Sq(saturate(1.0f - lightDir.y)) * 0.02f +
        0.002f;
    return exp(-(kRayleighCoeff + kMieCoeff + kOzoneCoeff * ozoneMultiplier) *
        lightExtinctionAmount * kAtmosphereDensity * multiplier * kLightTransmittanceScale);
}

static void GetRayleighMie(float opticalDepth, float densityR, float densityM, out float3 rayleigh, out float3 mie)
{
    rayleigh = (1.0f - exp(-opticalDepth * densityR * kRayleighCoeff / kRayleighMaxLum)) * kRayleighMaxLum;
    mie = (1.0f - exp(-opticalDepth * densityM * kMieCoeff / kMieMaxLum)) * kMieMaxLum;
}

static float3 GetAtmosphere(float3 rayStart, float3 rayDir, float rayLength, float3 lightDir, float3 lightColor, out float4 transmittance)
{
    rayStart.y = max(rayStart.y, 1.0f);

    float2 planetHit = PlanetIntersection(rayStart, rayDir);
    float2 atmosphereHit = AtmosphereIntersection(rayStart, rayDir);

    float normAltitude = rayStart.y / kAtmosphereHeight;
    if (atmosphereHit.y < 0.0f)
    {
        transmittance = float4(1.0f, 1.0f, 1.0f, 1.0f);
        return float3(0.0f, 0.0f, 0.0f);
    }

    atmosphereHit.y -= max(0.0f, atmosphereHit.x);

    float opticalDepth = planetHit.x > 0.0f ? min(planetHit.x, atmosphereHit.y) : atmosphereHit.y;
    opticalDepth = min(rayLength, opticalDepth);
    opticalDepth = min(opticalDepth * kAerialMul * kAerialScale, atmosphereHit.y);

    float hbias = 1.0f - 1.0f / (2.0f + Sq(atmosphereHit.y) * kDensityHeightMod);
    hbias = pow(hbias, 1.0f + normAltitude * kDensityCamMod);
    float sqhbias = Sq(hbias);
    float densityR = sqhbias * kAtmosphereDensity;
    float densityM = Sq(sqhbias) * hbias * kAtmosphereDensity;

    float adjustedLightY = lightDir.y;
    adjustedLightY += saturate(-lightDir.y + 0.02f) * saturate(lightDir.y + 0.7f);
    adjustedLightY = clamp(adjustedLightY, -1.0f, 1.0f);
    lightColor *= GetLightTransmittance(float3(lightDir.x, adjustedLightY, lightDir.z), hbias, kOzoneLightMul);

    float3 rayleigh;
    float3 mie;
    GetRayleighMie(opticalDepth, densityR, densityM, rayleigh, mie);

    float3 extinction =
        (kRayleighCoeff * densityR + kMieCoeff * densityM + kOzoneCoeff * densityR * kOzoneMul) *
        Pow4(1.0f - normAltitude) *
        kTransmittanceMul;

    float cosTheta = dot(rayDir, lightDir);
    float phaseR = PhaseR(cosTheta);
    float phaseM = PhaseM(cosTheta, 0.88f);

    float3 rayleighScattering = (phaseR + phaseR * kFakeMultiScatter) * lightColor + kNightLight * phaseR;
    float3 mieScattering = ((phaseM + phaseR * kFakeMultiScatter) * lightColor + kNightLight * phaseR) * kMieTint;
    float3 scattering = mieScattering * mie + rayleighScattering * rayleigh;

    transmittance.xyz = exp(-(opticalDepth + Pow8(opticalDepth * 4.5e-6f)) * extinction);
    transmittance.w = planetHit.x > 0.0f ? 0.0f : 1.0f;
    return scattering * kExposureMul;
}

static float3 GetSunDiscCircle(float3 rayDir, float3 lightDir, float3 lightColor, float transmittanceMask, float discSize)
{
    if (lightDir.y <= 0.0f || transmittanceMask <= 0.0f)
    {
        return float3(0.0f, 0.0f, 0.0f);
    }

    float minAngularRadius = 0.012f;
    float angularRadius = max(0.00436f * discSize, minAngularRadius);
    float edgeSoftness = max(angularRadius * 0.35f, 0.003f);
    float cosTheta = dot(rayDir, lightDir);
    float innerCos = cos(angularRadius);
    float outerCos = cos(angularRadius + edgeSoftness);
    float disc = smoothstep(outerCos, innerCos, cosTheta);
    return disc * lightColor * transmittanceMask;
}

static float3 SampleSunDiscCircleAA(float2 uv, float2 texelSize, float3 lightDir, float3 lightColor, float transmittanceMask, float discSize)
{
    static const float2 offsets[9] =
    {
        float2(0.0f, 0.0f),
        float2(-0.35f, -0.35f),
        float2(0.35f, -0.35f),
        float2(-0.35f, 0.35f),
        float2(0.35f, 0.35f),
        float2(0.0f, -0.5f),
        float2(0.0f, 0.5f),
        float2(-0.5f, 0.0f),
        float2(0.5f, 0.0f)
    };

    static const float weights[9] =
    {
        0.20f,
        0.10f,
        0.10f,
        0.10f,
        0.10f,
        0.10f,
        0.10f,
        0.10f,
        0.10f
    };

    float3 sum = float3(0.0f, 0.0f, 0.0f);
    for (int i = 0; i < 9; i++)
    {
        float2 sampleUv = uv + offsets[i] * texelSize;
        sampleUv.x = frac(sampleUv.x);
        sampleUv.y = saturate(sampleUv.y);
        float3 sampleDir = EquirectToDir(sampleUv);
        sum += GetSunDiscCircle(sampleDir, lightDir, lightColor, transmittanceMask, discSize) * weights[i];
    }

    return sum;
}

static float2 DirectionToCubeFaceUv(float3 dir, out float faceIndex)
{
    float3 absDir = abs(dir);
    float2 uv;

    if (absDir.x >= absDir.y && absDir.x >= absDir.z)
    {
        if (dir.x >= 0.0f)
        {
            faceIndex = 0.0f;
            uv = float2(-dir.z, dir.y) / absDir.x;
        }
        else
        {
            faceIndex = 1.0f;
            uv = float2(dir.z, dir.y) / absDir.x;
        }
    }
    else if (absDir.y >= absDir.z)
    {
        if (dir.y >= 0.0f)
        {
            faceIndex = 2.0f;
            uv = float2(dir.x, -dir.z) / absDir.y;
        }
        else
        {
            faceIndex = 3.0f;
            uv = float2(dir.x, dir.z) / absDir.y;
        }
    }
    else
    {
        if (dir.z >= 0.0f)
        {
            faceIndex = 4.0f;
            uv = float2(dir.x, dir.y) / absDir.z;
        }
        else
        {
            faceIndex = 5.0f;
            uv = float2(-dir.x, dir.y) / absDir.z;
        }
    }

    return uv * 0.5f + 0.5f;
}

static float3 CubeFaceUvToDirection(float2 uv, float faceIndex)
{
    float2 facePosition = uv * 2.0f - 1.0f;

    if (faceIndex < 0.5f)
    {
        return normalize(float3(1.0f, facePosition.y, -facePosition.x));
    }
    if (faceIndex < 1.5f)
    {
        return normalize(float3(-1.0f, facePosition.y, facePosition.x));
    }
    if (faceIndex < 2.5f)
    {
        return normalize(float3(facePosition.x, 1.0f, -facePosition.y));
    }
    if (faceIndex < 3.5f)
    {
        return normalize(float3(facePosition.x, -1.0f, facePosition.y));
    }
    if (faceIndex < 4.5f)
    {
        return normalize(float3(facePosition.x, facePosition.y, 1.0f));
    }

    return normalize(float3(-facePosition.x, facePosition.y, -1.0f));
}

static float3 SampleStarLayer(float3 dir, float cellCount, float threshold, float minSize, float maxSize, float intensityScale)
{
    float faceIndex;
    float2 faceUv = DirectionToCubeFaceUv(dir, faceIndex);
    float2 gridUv = faceUv * cellCount;
    float2 cell = floor(gridUv);
    float3 seed = float3(cell, faceIndex);

    float starChance = Hash13(seed);
    if (starChance < threshold)
    {
        return float3(0.0f, 0.0f, 0.0f);
    }

    float2 centerOffset = (Hash23(seed + 19.19f) - 0.5f) * 0.7f;
    float2 centerUv = (cell + 0.5f + centerOffset) / cellCount;
    float3 centerDir = CubeFaceUvToDirection(centerUv, faceIndex);

    // Measure the footprint on the unit sphere. A circle in cube-face UV space
    // becomes increasingly elliptical toward face edges and produces stretched
    // stars after the environment map is projected back onto the sky.
    float sphericalDistance = length(dir - centerDir) * (cellCount * 0.5f);

    float brightnessSeed = Hash13(seed + 3.71f);
    float size = lerp(maxSize, minSize, pow(brightnessSeed, 0.35f));
    float core = 1.0f - smoothstep(size, size + max(size * 0.75f, 0.01f), sphericalDistance);
    float sparkle = (1.0f - smoothstep(size * 0.3f, size * 1.6f, sphericalDistance)) * 0.15f;

    float starIntensity = (core + sparkle) * intensityScale * lerp(0.6f, 1.8f, pow(brightnessSeed, 6.0f));
    float temperature = Hash13(seed + 11.7f);
    float3 starColor = lerp(float3(1.0f, 0.92f, 0.84f), float3(0.72f, 0.82f, 1.0f), temperature);
    return starColor * starIntensity;
}

static float3 SampleProceduralStars(float3 dir)
{
    float3 stars = float3(0.0f, 0.0f, 0.0f);
    stars += SampleStarLayer(dir, 280.0f, 0.9975f, 0.03f, 0.09f, 1.2f);
    stars += SampleStarLayer(dir, 180.0f, 0.9955f, 0.04f, 0.14f, 2.4f);
    return stars;
}

static float3 NormalizeTint(float3 tint)
{
    tint = max(tint, float3(0.0f, 0.0f, 0.0f));
    float luminance = max(dot(tint, kLuminanceWeights), 1e-3f);
    return tint / luminance;
}

[numthreads(8, 8, 1)]
void ProceduralSkyboxCSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= width || id.y >= height)
    {
        return;
    }

    RWTexture2D<float4> environment = GetRWTexture(environmentHandle);

    float2 uv = (float2(id.xy) + 0.5f) / float2(width, height);
    float2 texelSize = 1.0f / float2(width, height);
    float3 dir = EquirectToDir(uv);
    float3 lightDir = normalize(float3(-sunDirectionIntensity.x, -sunDirectionIntensity.y, sunDirectionIntensity.z));
    float3 lightColor = max(sunColorSharpness.xyz, float3(0.0f, 0.0f, 0.0f)) * max(sunDirectionIntensity.w, 0.0f);

    float3 rayStart = float3(0.0f, kObserverHeight, 0.0f);
    float4 transmittance;
    float3 color = GetAtmosphere(rayStart, dir, kInfinity, lightDir, lightColor, transmittance);

    float sunDiscSize = sqrt(256.0f / max(sunColorSharpness.w, 1.0f));
    float3 sunDisc = SampleSunDiscCircleAA(uv, texelSize, lightDir, lightColor, transmittance.w, sunDiscSize);
    color += sunDisc;

    float zenithWeight = pow(saturate(dir.y), max(skyTop.w, 1e-3f));
    float horizonWeight = pow(saturate(1.0f - abs(dir.y)), 4.0f);
    float combinedSkyWeight = saturate(zenithWeight + horizonWeight);
    float3 skyTintBlend = skyTop.xyz * zenithWeight + skyHorizon.xyz * horizonWeight;
    float skyTintDenom = max(zenithWeight + horizonWeight, 1e-3f);
    float3 skyTint = NormalizeTint(skyTintBlend / skyTintDenom);
    color *= lerp(float3(1.0f, 1.0f, 1.0f), skyTint, combinedSkyWeight);

    float skyLuminance = dot(color, kLuminanceWeights);
    float skyDarkness = 1.0f - smoothstep(0.01f, 0.08f, skyLuminance);
    float sunDarkness = 1.0f - smoothstep(-0.18f, 0.05f, lightDir.y);
    float horizonStarFade = smoothstep(-0.05f, 0.12f, dir.y);
    float starVisibility = skyDarkness * sunDarkness * horizonStarFade * transmittance.w;
    color += SampleProceduralStars(dir) * starVisibility;

    float planetHit = 1.0f - transmittance.w;
    float groundWeight = saturate(-dir.y);
    float groundBlend = saturate(planetHit * groundWeight * (1.0f + max(skyHorizon.w, 0.0f)));
    float3 groundTint = max(ground.xyz, float3(0.0f, 0.0f, 0.0f));
    if (groundBlend > 0.0f)
    {
        // The lower hemisphere is a diffuse ground proxy for IBL when DDGI is unavailable.
        // Derive its radiance from the same sun and sky that illuminate the atmosphere instead
        // of imposing a minimum brightness, so it naturally darkens at night.
        float4 zenithTransmittance;
        float3 zenithSkyRadiance = GetAtmosphere(
            rayStart,
            float3(0.0f, 1.0f, 0.0f),
            kInfinity,
            lightDir,
            lightColor,
            zenithTransmittance);
        zenithSkyRadiance *= NormalizeTint(skyTop.xyz);

        float3 directGroundIrradiance = lightColor * GetLightTransmittance(lightDir, 1.0f, kOzoneLightMul)
            * saturate(lightDir.y);
        float3 groundRadiance = (zenithSkyRadiance + directGroundIrradiance * kInvPi) * groundTint;
        color = lerp(color, groundRadiance, groundBlend);
    }

    environment[id.xy] = float4(max(color, float3(0.0f, 0.0f, 0.0f)), 1.0f);
}
