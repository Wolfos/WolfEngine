import "ibl_common.slang";
import "constants.slang";
import "common_bindless.slang";

cbuffer BindlessHandles : register(b0)
{
    uint brdfHandle;
};

[numthreads(8, 8, 1)]
void IblBrdfCSMain(uint3 id : SV_DispatchThreadID)
{
    RWTexture2D<float4> brdfLutOut = GetRWTexture(brdfHandle);
    uint width, height;
    brdfLutOut.GetDimensions(width, height);
    if (id.x >= width || id.y >= height)
        return;

    float2 uv = (float2(id.xy) + 0.5f) / float2(width, height);
    float NdotV = uv.x;
    float roughness = uv.y;

    // Keep bindless texture/sampler argument buffers alive in Metal.
    if (brdfHandle == 0xFFFFFFFFu)
    {
        Texture2D<float4> dummyTex = GetTexture(brdfHandle);
        SamplerState dummySampler = GetSampler(brdfHandle);
        dummyTex.SampleLevel(dummySampler, float2(0.0f, 0.0f), 0.0f);
    }

    // Split-sum GGX BRDF integration
    const uint SAMPLE_COUNT = 128;
    float3 V;
    V.x = sqrt(1.0f - NdotV * NdotV);
    V.y = 0.0f;
    V.z = NdotV;

    float A = 0.0f;
    float B = 0.0f;

    for (uint i = 0; i < SAMPLE_COUNT; ++i)
    {
        float2 xi = Hammersley(i, SAMPLE_COUNT);
        float a = roughness * roughness;
        float phi = 2.0f * PI * xi.x;
        float cosTheta = sqrt((1.0f - xi.y) / (1.0f + (a * a - 1.0f) * xi.y));
        float sinTheta = sqrt(1.0f - cosTheta * cosTheta);

        float3 H = float3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);
        float3 L = normalize(2.0f * dot(V, H) * H - V);

        float NdotL = saturate(L.z);
        float NdotH = saturate(H.z);
        float VdotH = saturate(dot(V, H));

        if (NdotL > 0.0f)
        {
            float k = (roughness + 1.0f);
            k = (k * k) / 8.0f;
            float Gv = NdotV / max(NdotV * (1.0f - k) + k, 1e-4f);
            float Gl = NdotL / max(NdotL * (1.0f - k) + k, 1e-4f);
            float G = Gv * Gl;
            float Gvis = (G * VdotH) / max(NdotH * NdotV, 1e-4f);
            float Fc = pow(1.0f - VdotH, 5.0f);
            A += (1.0f - Fc) * Gvis;
            B += Fc * Gvis;
        }
    }

    A /= SAMPLE_COUNT;
    B /= SAMPLE_COUNT;

    brdfLutOut[id.xy] = float4(A, B, 0.0f, 1.0f);
}
