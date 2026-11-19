import "constants.slang";
import "common_bindless.slang";
import "common_lighting.slang";

/// Where a view's sky radiance comes from. In Procedural and Gradient mode it is the view's own equirectangular
/// environment; in HDRI mode it is the HDRI texture itself, which may be a cube. Filled by SkyEnvironmentSource
/// on the CPU. Scalars only, so the layout is the same on D3D12 and Metal.
struct SkyEnvironment
{
    uint handle;
    /// Non-zero when handle is a cube texture, read with GetTextureCube instead of DirToEnvUV.
    uint isCube;
    uint mipCount;
    /// Width of an equirectangular texture's top mip, or the face size of a cube.
    uint baseSize;
    /// Rotation about +Y applied to a direction before it is looked up.
    float yawCos;
    float yawSin;
    /// Multiplies the texture's radiance. Zero means there is no sky to read, as with an unloaded HDRI.
    float intensity;
    float padding;
};

float3 RotateSkyDirection(SkyEnvironment sky, float3 dir)
{
    return float3(
        sky.yawCos * dir.x + sky.yawSin * dir.z,
        dir.y,
        -sky.yawSin * dir.x + sky.yawCos * dir.z);
}

/// Sky radiance seen along world direction dir, from mip lod.
float3 SampleSky(SkyEnvironment sky, uint samplerHandle, float3 dir, float lod)
{
    if (sky.intensity <= 0.0f)
    {
        return 0.0f;
    }

    float3 skyDir = RotateSkyDirection(sky, dir);
    SamplerState linearSampler = GetSampler(samplerHandle);
    float3 radiance;
    if (sky.isCube != 0)
    {
        radiance = GetTextureCube(sky.handle).SampleLevel(linearSampler, skyDir, lod).rgb;
    }
    else
    {
        radiance = GetTexture(sky.handle).SampleLevel(linearSampler, DirToEnvUV(skyDir), lod).rgb;
    }

    return radiance * sky.intensity;
}

/// Solid angle of one top-mip texel of the sky texture around dir.
float SkyTexelSolidAngle(SkyEnvironment sky, float3 dir)
{
    float size = float(max(sky.baseSize, 1u));
    if (sky.isCube != 0)
    {
        return 4.0f * PI / (6.0f * size * size);
    }

    // A 2:1 equirect texel spans 2*PI/size in both longitude and latitude, shrinking toward the poles.
    float texelAngle = 2.0f * PI / size;
    float sinTheta = sqrt(saturate(1.0f - dir.y * dir.y));
    return texelAngle * texelAngle * max(sinTheta, 1e-4f);
}

/// Mip to read for one of sampleCount importance samples drawn with probability density pdf, so the samples
/// together cover the sphere without skipping bright texels between them. A single-mip texture always reads 0.
float SkyFilteredLod(SkyEnvironment sky, float3 dir, float pdf, uint sampleCount)
{
    if (sky.mipCount <= 1u)
    {
        return 0.0f;
    }

    float sampleSolidAngle = 1.0f / (float(sampleCount) * max(pdf, 1e-6f));
    float lod = 0.5f * log2(sampleSolidAngle / SkyTexelSolidAngle(sky, dir)) + 1.0f;

    return clamp(lod, 0.0f, float(sky.mipCount - 1u));
}
