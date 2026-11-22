// Raster displacement and terrain BLAS vertices must sample the same height at the same UV.
float TerrainHeightCubicHermite(float a, float b, float c, float d, float t)
{
    return b + 0.5f * t * (c - a + t * (2.0f * a - 5.0f * b + 4.0f * c - d + t * (3.0f * (b - c) + d - a)));
}

public float SampleTerrainHeightBicubic(
    Texture2D<float4> heightmap,
    uint width,
    uint height,
    float2 uv,
    float heightScale)
{
    if (width <= 1 || height <= 1)
    {
        return heightmap.Load(int3(0, 0, 0)).r * heightScale;
    }

    float2 samplePosition = saturate(uv) * float2(width - 1, height - 1);
    int2 baseTexel = int2(floor(samplePosition));
    float2 fraction = samplePosition - float2(baseTexel);
    float rows[4];
    [unroll]
    for (int row = 0; row < 4; row++)
    {
        int y = clamp(baseTexel.y + row - 1, 0, int(height) - 1);
        float samples[4];
        [unroll]
        for (int column = 0; column < 4; column++)
        {
            int x = clamp(baseTexel.x + column - 1, 0, int(width) - 1);
            samples[column] = heightmap.Load(int3(x, y, 0)).r;
        }

        rows[row] = TerrainHeightCubicHermite(samples[0], samples[1], samples[2], samples[3], fraction.x);
    }

    return saturate(TerrainHeightCubicHermite(rows[0], rows[1], rows[2], rows[3], fraction.y)) * heightScale;
}
