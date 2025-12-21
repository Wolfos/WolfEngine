import "ibl_common.slang";
import "common_bindless.slang";

cbuffer BindlessHandles : register(b0)
{
    uint environmentHandle;
    uint prefilterHandle;
    uint samplerHandle;
};

cbuffer PrefilterParams : register(b1)
{
    uint width;
    uint height;
    uint sliceCount;
    uint sliceHeight;
}

float3 ImportanceSampleGGX(float2 xi, float roughness, float3 n)
{
    float a = roughness * roughness;

    float phi = 2.0f * 3.14159265f * xi.x;
    float cosTheta = sqrt((1.0f - xi.y) / (1.0f + (a * a - 1.0f) * xi.y));
    float sinTheta = sqrt(1.0f - cosTheta * cosTheta);

    float3 h = float3(cos(phi) * sinTheta, cosTheta, sin(phi) * sinTheta);

    float3 t, b;
    BuildTangentBasis(n, t, b);
    return normalize(t * h.x + b * h.z + n * h.y);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= width || id.y >= height)
        return;

    Texture2D<float4> environmentTex = GetTexture(environmentHandle);
    RWTexture2D<float4> prefilterOut = GetRWTexture(prefilterHandle);
    SamplerState linearClamp = GetSampler(samplerHandle);

    uint slice = sliceHeight == 0 ? 0 : id.y / sliceHeight;
    slice = min(slice, sliceCount - 1);
    float localV = sliceHeight == 0 ? 0.0f : (float(id.y % sliceHeight) + 0.5f) / float(sliceHeight);

    float2 baseUV = float2((float(id.x) + 0.5f) / float(width), localV);
    float3 R = EquirectToDir(baseUV);
    float roughness = sliceCount > 1 ? float(slice) / float(sliceCount - 1) : 0.0f;

    const uint SAMPLE_COUNT = 128;
    float3 prefiltered = 0.0f;
    float totalWeight = 0.0f;

    [loop]
    for (uint i = 0; i < SAMPLE_COUNT; ++i)
    {
        float2 xi = Hammersley(i, SAMPLE_COUNT);
        float3 H = ImportanceSampleGGX(xi, roughness, R);
        float3 L = normalize(reflect(-R, H));

        float NdotL = saturate(dot(R, L));
        if (NdotL > 0.0f)
        {
            float2 envUV = DirToEquirect(L);
            float3 sampleCol = environmentTex.SampleLevel(linearClamp, envUV, 0.0f).rgb;
            prefiltered += sampleCol * NdotL;
            totalWeight += NdotL;
        }
    }

    prefiltered = totalWeight > 0.0f ? prefiltered / totalWeight : 0.0f;
    prefilterOut[id.xy] = float4(prefiltered, 1.0f);
}
