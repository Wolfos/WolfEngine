import "../Common/common_bindless.slang";
import "../ThirdParty/Hillaire/sky_atmosphere.slang";
import "atmosphere_common.slang";

cbuffer BindlessHandles : register(b0)
{
    uint outputHandle;
    uint samplerHandle;
};

cbuffer AtmosphereParams : register(b1)
{
    AtmosphereConstants atmosphere;
};

/// The reference's RenderTransmittanceLutPS as a compute shader.
[numthreads(8, 8, 1)]
void AtmosphereTransmittanceLutCSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= uint(TRANSMITTANCE_TEXTURE_WIDTH) || id.y >= uint(TRANSMITTANCE_TEXTURE_HEIGHT))
    {
        return;
    }

    float2 pixPos = float2(id.xy) + 0.5f;
    AtmosphereParameters Atmosphere = GetAtmosphereParameters(atmosphere);
    AtmosphereLuts luts = MakeAtmosphereLuts(0u, 0u, samplerHandle);

    // Compute camera position from LUT coords
    float2 uv = (pixPos) / float2(TRANSMITTANCE_TEXTURE_WIDTH, TRANSMITTANCE_TEXTURE_HEIGHT);
    float viewHeight;
    float viewZenithCosAngle;
    UvToLutTransmittanceParams(Atmosphere, viewHeight, viewZenithCosAngle, uv);

    //  A few extra needed constants
    float3 WorldPos = float3(0.0f, 0.0f, viewHeight);
    float3 WorldDir = float3(0.0f, sqrt(1.0 - viewZenithCosAngle * viewZenithCosAngle), viewZenithCosAngle);

    const bool ground = false;
    const float SampleCountIni = 40.0f; // Can go a low as 10 sample but energy lost starts to be visible.
    const bool VariableSampleCount = false;
    const bool MieRayPhase = false;
    float3 transmittance = exp(-IntegrateScatteredLuminance(
        WorldPos, WorldDir, atmosphere.sunDirection.xyz, Atmosphere, luts,
        1.0f, false, ATMOSPHERE_VIEW_RAY_MARCH_MIN_MAX_SPP,
        ground, SampleCountIni, VariableSampleCount, MieRayPhase).OpticalDepth);

    GetRWTexture(outputHandle)[id.xy] = float4(transmittance, 1.0f);
}
