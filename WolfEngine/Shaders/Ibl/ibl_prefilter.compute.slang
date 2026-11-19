import "ibl_common.slang";
import "../Common/constants.slang";
import "../Common/common_bindless.slang";
import "../Common/common_lighting.slang";
import "../Common/sky_sampling.slang";

cbuffer BindlessHandles : register(b0)
{
    SkyEnvironment skyEnvironment;
    uint prefilterHandle;
    uint samplerHandle;
    uint width;
    uint height;
    uint sliceCount;
    uint sliceHeight;
};

float3 ImportanceSampleGGX(float2 xi, float roughness, float3 n)
{
    float a = roughness * roughness;

    float phi = 2.0f * PI * xi.x;
    float cosTheta = sqrt((1.0f - xi.y) / (1.0f + (a * a - 1.0f) * xi.y));
    float sinTheta = sqrt(1.0f - cosTheta * cosTheta);

    float3 h = float3(cos(phi) * sinTheta, cosTheta, sin(phi) * sinTheta);

    float3 t, b;
    BuildTangentBasis(n, t, b);
    return normalize(t * h.x + b * h.z + n * h.y);
}

[numthreads(8, 8, 1)]
void IblPrefilterCSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= width || id.y >= height)
        return;

    RWTexture2D<float4> prefilterOut = GetRWTexture(prefilterHandle);

    uint slice = sliceHeight == 0 ? 0 : id.y / sliceHeight;
    slice = min(slice, sliceCount - 1);
    float localV = sliceHeight == 0 ? 0.0f : (float(id.y % sliceHeight) + 0.5f) / float(sliceHeight);

    float2 baseUV = float2((float(id.x) + 0.5f) / float(width), localV);
    // Stored the way consumers read it, at DirToEnvUV of the reflection direction.
    float3 R = EnvUVToDir(baseUV);
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
            // With N = V = R, the density of L is D(NdotH) / 4. Alpha is kept above zero so the mirror slice
            // gets a large density, and with it the top mip, instead of D's zero.
            float NdotH = saturate(dot(R, H));
            float alpha = max(roughness * roughness, 1e-3f);
            float pdf = DistributionGGX(NdotH, alpha) * 0.25f;
            float lod = SkyFilteredLod(skyEnvironment, L, pdf, SAMPLE_COUNT);
            float3 sampleCol = SampleSky(skyEnvironment, samplerHandle, L, lod);
            prefiltered += sampleCol * NdotL;
            totalWeight += NdotL;
        }
    }

    prefiltered = totalWeight > 0.0f ? prefiltered / totalWeight : 0.0f;
    prefilterOut[id.xy] = float4(prefiltered, 1.0f);
}
