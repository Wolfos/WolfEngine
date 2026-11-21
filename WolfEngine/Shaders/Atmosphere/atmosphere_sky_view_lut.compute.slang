import "../Common/common_bindless.slang";
import "../ThirdParty/Hillaire/sky_atmosphere.slang";
import "atmosphere_common.slang";

cbuffer BindlessHandles : register(b0)
{
    uint outputHandle;
    uint transmittanceLutHandle;
    uint multiScatteringLutHandle;
    uint samplerHandle;
};

cbuffer AtmosphereParams : register(b1)
{
    AtmosphereConstants atmosphere;
};

/// The reference's SkyViewLutPS as a compute shader. Rays that reach the ground also get the reference's ground
/// term, the lit albedo seen through the atmosphere, so the sky below the horizon matches what the planet would show.
[numthreads(8, 8, 1)]
void AtmosphereSkyViewLutCSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= uint(SKY_VIEW_LUT_WIDTH) || id.y >= uint(SKY_VIEW_LUT_HEIGHT))
    {
        return;
    }

    float2 pixPos = float2(id.xy) + 0.5f;
    AtmosphereParameters Atmosphere = GetAtmosphereParameters(atmosphere);
    AtmosphereLuts luts = MakeAtmosphereLuts(transmittanceLutHandle, multiScatteringLutHandle, samplerHandle);

    float3 WorldPos = GetAtmosphereCameraPosition(atmosphere);
    float2 uv = pixPos / float2(SKY_VIEW_LUT_WIDTH, SKY_VIEW_LUT_HEIGHT);

    float viewHeight = length(WorldPos);

    float viewZenithCosAngle;
    float lightViewCosAngle;
    UvToSkyViewLutParams(Atmosphere, viewZenithCosAngle, lightViewCosAngle, viewHeight, uv);

    float3 SunDir;
    {
        float3 UpVector = WorldPos / viewHeight;
        float sunZenithCosAngle = dot(UpVector, atmosphere.sunDirection.xyz);
        SunDir = normalize(float3(sqrt(max(1.0 - sunZenithCosAngle * sunZenithCosAngle, 0.0f)), 0.0, sunZenithCosAngle));
    }

    WorldPos = float3(0.0f, 0.0f, viewHeight);

    // max(0, ...) keeps rounding at the poles and at a cosine of -1 from producing a NaN.
    float viewZenithSinAngle = sqrt(max(1 - viewZenithCosAngle * viewZenithCosAngle, 0.0f));
    float3 WorldDir = float3(
        viewZenithSinAngle * lightViewCosAngle,
        viewZenithSinAngle * sqrt(max(1.0 - lightViewCosAngle * lightViewCosAngle, 0.0f)),
        viewZenithCosAngle);

    // Move to top atmospehre
    if (!MoveToTopAtmosphere(WorldPos, WorldDir, Atmosphere.TopRadius))
    {
        // Ray is not intersecting the atmosphere
        GetRWTexture(outputHandle)[id.xy] = float4(0, 0, 0, 1);
        return;
    }

    const bool ground = true;
    const float SampleCountIni = 30;
    const bool VariableSampleCount = true;
    const bool MieRayPhase = true;
    SingleScatteringResult ss = IntegrateScatteredLuminance(
        WorldPos, WorldDir, SunDir, Atmosphere, luts,
        atmosphere.sunIlluminance.rgb, true, ATMOSPHERE_VIEW_RAY_MARCH_MIN_MAX_SPP,
        ground, SampleCountIni, VariableSampleCount, MieRayPhase);

    GetRWTexture(outputHandle)[id.xy] = float4(ss.L, 1);
}
