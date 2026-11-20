import "../Common/common_bindless.slang";

/// Generates the cloud noise textures once: tileable Perlin-Worley shape noise, Worley detail noise, and the
/// procedural weather map. Every function wraps at its integer period so the textures tile seamlessly.

cbuffer BindlessHandles : register(b0)
{
    uint outputHandle;
    uint outputSize;
    uint _noisePadding0;
    uint _noisePadding1;
};

uint CloudNoiseHash(uint3 value)
{
    // PCG3D (Jarzynski and Olano 2020), folded to one channel.
    uint3 v = value * 1664525u + 1013904223u;
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;

    return v.x ^ v.y ^ v.z;
}

float3 CloudNoiseHash3(uint3 value, uint seed)
{
    uint h0 = CloudNoiseHash(value + uint3(seed, 0u, 0u));
    uint h1 = CloudNoiseHash(value + uint3(0u, seed, 7u));
    uint h2 = CloudNoiseHash(value + uint3(13u, 0u, seed));

    return float3(h0 & 0x00ffffffu, h1 & 0x00ffffffu, h2 & 0x00ffffffu) / 16777216.0f;
}

int3 WrapCell(int3 cell, int period)
{
    return ((cell % period) + period) % period;
}

float3 PerlinFade(float3 t)
{
    return t * t * t * (t * (t * 6.0f - 15.0f) + 10.0f);
}

float3 PerlinGradient(int3 cell, uint seed)
{
    float3 random = CloudNoiseHash3(uint3(cell), seed) * 2.0f - 1.0f;

    return normalize(random + 1e-4f);
}

/// Gradient noise in about [-1, 1] that repeats every period units.
float TileablePerlin(float3 position, int period, uint seed)
{
    float3 cellFloor = floor(position);
    float3 fraction = position - cellFloor;
    int3 cell = int3(cellFloor);
    float corners[8];
    for (uint i = 0u; i < 8u; i++)
    {
        int3 offset = int3(i & 1u, (i >> 1u) & 1u, (i >> 2u) & 1u);
        float3 gradient = PerlinGradient(WrapCell(cell + offset, period), seed);
        corners[i] = dot(gradient, fraction - float3(offset));
    }

    float3 fade = PerlinFade(fraction);
    float x00 = lerp(corners[0], corners[1], fade.x);
    float x10 = lerp(corners[2], corners[3], fade.x);
    float x01 = lerp(corners[4], corners[5], fade.x);
    float x11 = lerp(corners[6], corners[7], fade.x);
    float y0 = lerp(x00, x10, fade.y);
    float y1 = lerp(x01, x11, fade.y);

    return lerp(y0, y1, fade.z) * 1.5f;
}

float TileablePerlinFbm(float3 uvw, int frequency, uint octaves, uint seed)
{
    float sum = 0.0f;
    float amplitude = 1.0f;
    float totalAmplitude = 0.0f;
    int octaveFrequency = frequency;
    for (uint octave = 0u; octave < octaves; octave++)
    {
        sum += TileablePerlin(uvw * float(octaveFrequency), octaveFrequency, seed + octave * 31u) * amplitude;
        totalAmplitude += amplitude;
        amplitude *= 0.5f;
        octaveFrequency *= 2;
    }

    return sum / totalAmplitude;
}

/// Distance to the nearest of one random feature point per cell, with cellCount cells per unit, in about [0, 1].
float TileableWorley(float3 uvw, int cellCount, uint seed)
{
    float3 position = uvw * float(cellCount);
    int3 cell = int3(floor(position));
    float nearest = 1e10f;
    for (int z = -1; z <= 1; z++)
    {
        for (int y = -1; y <= 1; y++)
        {
            for (int x = -1; x <= 1; x++)
            {
                int3 neighbour = cell + int3(x, y, z);
                float3 feature = float3(neighbour) + CloudNoiseHash3(uint3(WrapCell(neighbour, cellCount)), seed);
                float3 offset = position - feature;
                nearest = min(nearest, dot(offset, offset));
            }
        }
    }

    return saturate(sqrt(nearest));
}

/// Inverted Worley over three octaves: billowy cells that are bright at their centres.
float WorleyFbm(float3 uvw, int cellCount, uint seed)
{
    float octave0 = 1.0f - TileableWorley(uvw, cellCount, seed);
    float octave1 = 1.0f - TileableWorley(uvw, cellCount * 2, seed + 1u);
    float octave2 = 1.0f - TileableWorley(uvw, cellCount * 4, seed + 2u);

    return octave0 * 0.625f + octave1 * 0.25f + octave2 * 0.125f;
}

float NoiseRemap(float value, float oldMin, float oldMax, float newMin, float newMax)
{
    return newMin + (value - oldMin) / max(oldMax - oldMin, 1e-5f) * (newMax - newMin);
}

/// Shape noise. R: Perlin-Worley, the cloud bodies. GBA: Worley FBM at rising frequencies, which erode them.
[numthreads(4, 4, 4)]
void CloudNoiseShapeCS(uint3 id : SV_DispatchThreadID)
{
    if (any(id >= outputSize))
    {
        return;
    }

    float3 uvw = (float3(id) + 0.5f) / float(outputSize);
    float perlin = saturate(TileablePerlinFbm(uvw, 4, 5u, 11u) * 0.7f + 0.5f);
    float worley = WorleyFbm(uvw, 4, 101u);
    float perlinWorley = saturate(NoiseRemap(perlin, 0.0f, 1.0f, worley, 1.0f));

    float4 value = float4(
        perlinWorley,
        WorleyFbm(uvw, 4, 201u),
        WorleyFbm(uvw, 8, 301u),
        WorleyFbm(uvw, 16, 401u));

    GetRWTexture3D(outputHandle)[id] = value;
}

/// Detail noise: Worley FBM at three rising frequencies, which erode the cloud edges into wisps and billows.
[numthreads(4, 4, 4)]
void CloudNoiseDetailCS(uint3 id : SV_DispatchThreadID)
{
    if (any(id >= outputSize))
    {
        return;
    }

    float3 uvw = (float3(id) + 0.5f) / float(outputSize);
    float4 value = float4(
        WorleyFbm(uvw, 2, 501u),
        WorleyFbm(uvw, 4, 601u),
        WorleyFbm(uvw, 8, 701u),
        1.0f);

    GetRWTexture3D(outputHandle)[id] = value;
}

/// Procedural weather map. R: where clouds can form, clumped into fields of individual clouds. G: cloud type,
/// from low flat clouds (0) to towering cumulus (1). B: density variation between clouds.
[numthreads(8, 8, 1)]
void CloudNoiseWeatherCS(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= outputSize))
    {
        return;
    }

    // A thin slab of the 3D noise, sampled at a cell-aligned depth so it tiles in X and Y.
    float3 uvw = float3((float2(id.xy) + 0.5f) / float(outputSize), 0.25f);
    float fields = saturate(TileablePerlinFbm(uvw, 6, 5u, 811u) * 0.8f + 0.5f);
    float clumps = 1.0f - TileableWorley(uvw, 12, 821u);
    float clumpsFine = 1.0f - TileableWorley(uvw, 24, 822u);
    float coverage = saturate(fields * (0.3f + 0.5f * clumps + 0.2f * clumpsFine) * 1.45f);

    float cloudType = saturate(TileablePerlinFbm(uvw, 3, 4u, 831u) * 1.1f + 0.55f);
    float density = saturate(TileablePerlinFbm(uvw, 8, 3u, 841u) * 0.8f + 0.5f);

    GetRWTexture(outputHandle)[id.xy] = float4(coverage, cloudType, density, 1.0f);
}
