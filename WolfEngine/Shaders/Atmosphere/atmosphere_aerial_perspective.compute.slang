import "../Common/common.slang";
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

cbuffer ViewParams : register(b2)
{
    /// Camera-relative inverse view-projection, as ReconstructWorldDirection expects.
    float4x4 invViewProjection;
};

static const uint AERIAL_PERSPECTIVE_SIZE = 32u;

/// The reference's RenderCameraVolumePS as a compute shader: one thread per froxel of the camera's view volume.
[numthreads(4, 4, 4)]
void AtmosphereAerialPerspectiveCSMain(uint3 id : SV_DispatchThreadID)
{
    if (any(id >= AERIAL_PERSPECTIVE_SIZE))
    {
        return;
    }

    RWTexture3D<float4> output = GetRWTexture3D(outputHandle);
    AtmosphereParameters Atmosphere = GetAtmosphereParameters(atmosphere);
    AtmosphereLuts luts = MakeAtmosphereLuts(transmittanceLutHandle, multiScatteringLutHandle, samplerHandle);

    float3 worldDirection = ReconstructWorldDirection(id.xy, uint2(AERIAL_PERSPECTIVE_SIZE, AERIAL_PERSPECTIVE_SIZE), 1.0f, invViewProjection);
    float3 WorldDir = normalize(ToAtmosphereDirection(worldDirection));

    float3 camPos = GetAtmosphereCameraPosition(atmosphere);
    float3 SunDir = atmosphere.sunDirection.xyz;

    float Slice = ((float(id.z) + 0.5f) / AP_SLICE_COUNT);
    Slice *= Slice; // squared distribution
    Slice *= AP_SLICE_COUNT;

    float3 WorldPos = camPos;
    float viewHeight;

    // Compute position from froxel information
    float tMax = AerialPerspectiveSliceToDepth(Slice);
    float3 newWorldPos = WorldPos + tMax * WorldDir;

    // If the voxel is under the ground, make sure to offset it out on the ground.
    viewHeight = length(newWorldPos);
    if (viewHeight <= (Atmosphere.BottomRadius + PLANET_RADIUS_OFFSET))
    {
        // Apply a position offset to make sure no artefact are visible close to the earth boundaries for large voxel.
        newWorldPos = normalize(newWorldPos) * (Atmosphere.BottomRadius + PLANET_RADIUS_OFFSET + 0.001f);
        WorldDir = normalize(newWorldPos - camPos);
        tMax = length(newWorldPos - camPos);
    }
    float tMaxMax = tMax;

    // The camera is always kept inside the atmosphere (AtmosphereModel.GetCameraAltitudeKm), so the reference's
    // move-to-top-of-atmosphere step for cameras in space never applies.

    const bool ground = false;
    const float SampleCountIni = max(1.0, float(id.z + 1.0) * 2.0f);
    const bool VariableSampleCount = false;
    const bool MieRayPhase = true;
    SingleScatteringResult ss = IntegrateScatteredLuminance(
        WorldPos, WorldDir, SunDir, Atmosphere, luts,
        atmosphere.sunIlluminance.rgb, true, ATMOSPHERE_VIEW_RAY_MARCH_MIN_MAX_SPP,
        ground, SampleCountIni, VariableSampleCount, MieRayPhase, tMaxMax);

    const float Transmittance = dot(ss.Transmittance, float3(1.0f / 3.0f, 1.0f / 3.0f, 1.0f / 3.0f));
    output[id] = float4(ss.L, 1.0 - Transmittance);
}
