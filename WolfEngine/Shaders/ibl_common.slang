import "constants.slang";

static float3 EquirectToDir(float2 uv)
{
    float phi = (uv.x * 2.0f - 1.0f) * PI;
    float theta = uv.y * PI;
    float sinTheta = sin(theta);
    return float3(cos(phi) * sinTheta, cos(theta), sin(phi) * sinTheta);
}

static float2 DirToEquirect(float3 dir)
{
    dir = normalize(dir);
    float phi = atan2(dir.z, dir.x);
    float theta = acos(clamp(dir.y, -1.0f, 1.0f));
    float u = phi / (2.0f * PI) + 0.5f;
    float v = theta / PI;
    return float2(u, v);
}

// Low-discrepancy sequence
static float RadicalInverse_VdC(uint bits)
{
    bits = (bits << 16u) | (bits >> 16u);
    bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
    bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
    bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
    bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
    return float(bits) * 2.3283064365386963e-10; // / 0x100000000
}

static float2 Hammersley(uint i, uint N)
{
    return float2(float(i) / float(N), RadicalInverse_VdC(i));
}

static void BuildTangentBasis(float3 n, out float3 t, out float3 b)
{
    float sign = n.z >= 0.0f ? 1.0f : -1.0f;
    float a = -1.0f / (sign + n.z);
    float bVal = n.x * n.y * a;
    t = float3(1.0f + sign * n.x * n.x * a, sign * bVal, -sign * n.x);
    b = float3(bVal, sign + n.y * n.y * a, -n.y);
}
