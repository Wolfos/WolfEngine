import "ibl_common.slang";
import "common_bindless.slang";

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
    float4 sunColorSharpness; // xyz color, w sharpness
    float4 skyTop; // xyz color, w horizonPower
    float4 skyHorizon; // xyz color, w groundBlend
    float4 ground; // xyz color, w unused
};

[numthreads(8, 8, 1)]
void ProceduralSkyboxCSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= width || id.y >= height)
        return;

    RWTexture2D<float4> environment = GetRWTexture(environmentHandle);
    float2 uv = (float2(id.xy) + 0.5f) / float2(width, height);
    float3 dir = EquirectToDir(uv);

    float3 sunDir = normalize(float3(-sunDirectionIntensity.x, -sunDirectionIntensity.y, sunDirectionIntensity.z));
    float sunDot = saturate(dot(dir, sunDir));
    float sun = pow(sunDot, sunColorSharpness.w) * sunDirectionIntensity.w;

    float skyFactor = saturate(dir.y * 0.5f + 0.5f);
    skyFactor = pow(skyFactor, skyTop.w);
    float3 skyColor = lerp(skyHorizon.xyz, skyTop.xyz, skyFactor);

    float groundFactor = saturate(-dir.y);
    float3 baseColor = lerp(skyColor, ground.xyz, groundFactor * skyHorizon.w);

    float3 color = baseColor + sun * sunColorSharpness.xyz;
    environment[id.xy] = float4(color, 1.0f);
}
