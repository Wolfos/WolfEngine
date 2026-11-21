import "../Common/common_bindless.slang";
import "../Common/common_lighting.slang";
import "atmosphere_common.slang";

cbuffer BindlessHandles : register(b0)
{
    uint environmentHandle;
    uint skyViewLutHandle;
    uint samplerHandle;
    uint width;
    uint height;
};

cbuffer AtmosphereParams : register(b1)
{
    AtmosphereConstants atmosphere;
};

/// Fills the view's equirectangular environment, which image-based lighting and DDGI sky misses read, from the
/// Sky View LUT. The sun's disc is left out: the directional light already lights the scene with the sun, and
/// counting the disc again here would add its light a second time through the IBL and DDGI.
[numthreads(8, 8, 1)]
void AtmosphereEnvironmentCSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= width || id.y >= height)
    {
        return;
    }

    float2 uv = (float2(id.xy) + 0.5f) / float2(width, height);
    float3 direction = EnvUVToDir(uv);
    float3 radiance = SampleSkyViewLut(atmosphere, skyViewLutHandle, samplerHandle, direction);

    GetRWTexture(environmentHandle)[id.xy] = float4(radiance, 1.0f);
}
