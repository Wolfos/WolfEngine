import "ibl_common.slang";
import "../Common/constants.slang";
import "../Common/common_bindless.slang";

cbuffer BindlessHandles : register(b0)
{
    uint environmentHandle;
    uint irradianceHandle;
    uint samplerHandle;
    uint width;
    uint height;
    uint sliceCount;     // unused here
    uint sliceHeight;    // unused here
};

static float3 ImportanceSampleHemisphere(uint i, uint sampleCount, float3 n)
{
    float2 xi = Hammersley(i, sampleCount);
    float phi = xi.y * 2.0f * PI;
    float cosTheta = 1.0f - xi.x;
    float sinTheta = sqrt(saturate(1.0f - cosTheta * cosTheta));

    float3 l = float3(cos(phi) * sinTheta, cosTheta, sin(phi) * sinTheta);

    float3 t, b;
    BuildTangentBasis(n, t, b);
    return normalize(t * l.x + b * l.z + n * l.y);
}

[numthreads(8, 8, 1)]
void IblIrradianceCSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= width || id.y >= height)
        return;

    Texture2D<float4> environmentTex = GetTexture(environmentHandle);
    RWTexture2D<float4> irradianceOut = GetRWTexture(irradianceHandle);
    SamplerState linearClamp = GetSampler(samplerHandle);

    float2 uv = (float2(id.xy) + 0.5f) / float2(width, height);
    float3 n = EquirectToDir(uv);

    const uint SAMPLE_COUNT = 1024;
    float3 sum = 0.0f;
    float weight = 0.0f;
    [loop]
    for (uint i = 0; i < SAMPLE_COUNT; ++i)
    {
        float3 l = ImportanceSampleHemisphere(i, SAMPLE_COUNT, n);
        float NdotL = saturate(dot(n, l));
        if (NdotL > 0.0f)
        {
            float2 envUV = DirToEquirect(l);
            envUV.x = frac(envUV.x);
            envUV.y = saturate(envUV.y);
            float3 c = environmentTex.SampleLevel(linearClamp, envUV, 0.0f).rgb;
            sum += c * NdotL;
            weight += NdotL;
        }
    }

    float3 irradiance = weight > 0.0f ? (sum / weight) * PI : 0.0f;
    irradianceOut[id.xy] = float4(irradiance, 1.0f);
}
