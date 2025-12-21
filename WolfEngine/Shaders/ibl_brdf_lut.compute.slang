import "common_bindless.slang";

cbuffer BindlessHandles : register(b0)
{
    uint brdfHandle;
};

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    RWTexture2D<float4> brdfLutOut = GetRWTexture(brdfHandle);
    uint width, height;
    brdfLutOut.GetDimensions(width, height);
    if (id.x >= width || id.y >= height)
        return;

    float2 uv = (float2(id.xy) + 0.5f) / float2(width, height);
    float NdotV = uv.x;
    float roughness = uv.y;

    // Heitz-style BRDF integration
    const uint SAMPLE_COUNT = 128;
    float3 V;
    V.x = sqrt(1.0f - NdotV * NdotV);
    V.y = 0.0f;
    V.z = NdotV;

    float A = 0.0f;
    float B = 0.0f;

    for (uint i = 0; i < SAMPLE_COUNT; ++i)
    {
        float2 xi;
        // simple Hammersley
        xi.x = float(i) / float(SAMPLE_COUNT);
        uint bits = i;
        bits = (bits << 16u) | (bits >> 16u);
        bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
        bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
        bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
        bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
        xi.y = float(bits) * 2.3283064365386963e-10;

        float a = roughness * roughness;
        float phi = 2.0f * 3.14159265f * xi.x;
        float cosTheta = sqrt((1.0f - xi.y) / (1.0f + (a * a - 1.0f) * xi.y));
        float sinTheta = sqrt(1.0f - cosTheta * cosTheta);

        float3 H = float3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);
        float3 L = normalize(2.0f * dot(V, H) * H - V);

        float NdotL = saturate(L.z);
        float NdotH = saturate(H.z);
        float VdotH = saturate(dot(V, H));

        if (NdotL > 0.0f)
        {
            float G = (2.0f * NdotH * NdotV) / max(VdotH, 1e-4f);
            float G2 = (2.0f * NdotH * NdotL) / max(VdotH, 1e-4f);
            float Gvis = (G * G2) * 0.5f;
            float Fc = pow(1.0f - VdotH, 5.0f);
            A += (1.0f - Fc) * Gvis;
            B += Fc * Gvis;
        }
    }

    A /= SAMPLE_COUNT;
    B /= SAMPLE_COUNT;

    brdfLutOut[id.xy] = float4(A, B, 0.0f, 1.0f);
}
