// What the foliage cull and detail generation share: the chunk record and the packed instance's rotation. See
// docs/foliage.md.

// Mirrors GpuFoliageChunk in TerrainFoliageRuntime.cs: up to 64 instances of one cell, quantised across the cell's
// bounds. cullMin.w is the longest cull distance in the chunk, cullMax.w the longest shadow distance.
struct GpuFoliageChunk
{
    float4 quantizationMin;
    float4 quantizationMax;
    float4 cullMin;
    float4 cullMax;
    uint instanceStart;
    uint instanceCount;
    uint pad0;
    uint pad1;
};

// Three 10-bit components in [-1/sqrt(2), 1/sqrt(2)] in 1022 steps, so zero is exact, most significant first, and the
// index of the left-out largest in the top two bits. Mirrors FoliageInstancePacking.
static const float FOLIAGE_QUATERNION_LIMIT = 0.70710678f;
static const float FOLIAGE_QUATERNION_STEPS = 1022.0f;

// The type index packed instances use for a slot with no plant in it. Mirrors FoliageInstancePacking.MaxTypeIndex.
static const uint FOLIAGE_EMPTY_TYPE_INDEX = 0xFFFFu;

float4 UnpackFoliageQuaternion(uint packed)
{
    uint largest = packed >> 30;
    float smallest[3];
    smallest[0] = ((float((packed >> 20) & 0x3FFu) / FOLIAGE_QUATERNION_STEPS) * 2.0f - 1.0f) * FOLIAGE_QUATERNION_LIMIT;
    smallest[1] = ((float((packed >> 10) & 0x3FFu) / FOLIAGE_QUATERNION_STEPS) * 2.0f - 1.0f) * FOLIAGE_QUATERNION_LIMIT;
    smallest[2] = ((float(packed & 0x3FFu) / FOLIAGE_QUATERNION_STEPS) * 2.0f - 1.0f) * FOLIAGE_QUATERNION_LIMIT;
    float largestValue = sqrt(max(0.0f, 1.0f - dot(float3(smallest[0], smallest[1], smallest[2]),
                                                   float3(smallest[0], smallest[1], smallest[2]))));

    float4 q;
    uint next = 0;
    for (uint i = 0; i < 4; i++)
    {
        float value = largestValue;
        if (i != largest)
        {
            value = smallest[next];
            next++;
        }

        q[i] = value;
    }

    return normalize(q);
}

// Mirrors FoliageInstancePacking.PackQuaternion. q is (x, y, z, w).
uint PackFoliageQuaternion(float4 q)
{
    float lengthSquared = dot(q, q);
    q = lengthSquared > 0.0f ? q * rsqrt(lengthSquared) : float4(0.0f, 0.0f, 0.0f, 1.0f);

    uint largest = 0u;
    for (uint i = 1u; i < 4u; i++)
    {
        if (abs(q[i]) > abs(q[largest]))
        {
            largest = i;
        }
    }

    // q and -q are the same rotation, so the largest component is made positive and left out.
    float sign = q[largest] < 0.0f ? -1.0f : 1.0f;
    uint packed = largest << 30;
    uint shift = 20u;
    for (uint i = 0u; i < 4u; i++)
    {
        if (i == largest)
        {
            continue;
        }

        float normalized = (q[i] * sign / FOLIAGE_QUATERNION_LIMIT) * 0.5f + 0.5f;
        uint quantized = uint(round(saturate(normalized) * FOLIAGE_QUATERNION_STEPS));
        packed |= quantized << shift;
        shift -= 10u;
    }

    return packed;
}
