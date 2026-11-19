import "../Common/constants.slang";
import "../Common/common_bindless.slang";
import "../Common/common_lighting.slang";

cbuffer BindlessHandles : register(b0)
{
    uint environmentHandle;
    uint width;
    uint height;
};

cbuffer GradientParams : register(b1)
{
    float4 zenithColor;
    float4 horizonColor;
    float4 groundColor;
    /// x: sky exponent, y: ground exponent, z: intensity.
    float4 shape;
};

[numthreads(8, 8, 1)]
void GradientSkyCSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= width || id.y >= height)
    {
        return;
    }

    RWTexture2D<float4> environment = GetRWTexture(environmentHandle);

    float2 uv = (float2(id.xy) + 0.5f) / float2(width, height);
    float3 dir = EnvUVToDir(uv);

    float3 color;
    if (dir.y >= 0.0f)
    {
        float t = pow(saturate(dir.y), max(shape.x, 1e-3f));
        color = lerp(horizonColor.rgb, zenithColor.rgb, t);
    }
    else
    {
        float t = pow(saturate(-dir.y), max(shape.y, 1e-3f));
        color = lerp(horizonColor.rgb, groundColor.rgb, t);
    }

    environment[id.xy] = float4(max(color, 0.0f) * max(shape.z, 0.0f), 1.0f);
}
