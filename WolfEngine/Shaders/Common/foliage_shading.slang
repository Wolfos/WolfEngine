// The foliage shading model's surface terms, shared by the G-buffer pass and ray-traced hit shading so a leaf
// has the same albedo and normal whether it is seen directly, in a reflection or by DDGI. See docs/foliage.md.
import "draw_data.slang";

// The G-buffer normal target's alpha. Standard surfaces write 1; foliage writes this base plus its
// translucency, so the lighting pass can tell the two apart from a single half-float channel.
static const float GBUFFER_FOLIAGE_NORMAL_ALPHA_BASE = 2.0f;
static const float GBUFFER_FOLIAGE_NORMAL_ALPHA_THRESHOLD = 1.5f;

bool IsMaterialFoliage(GpuMaterialData material)
{
    return (material.materialFlags & GPU_MATERIAL_FLAG_FOLIAGE) != 0u;
}

uint GetFoliageNormalMode(GpuMaterialData material)
{
    return (material.materialFlags >> GPU_MATERIAL_FOLIAGE_NORMAL_MODE_SHIFT) & GPU_MATERIAL_FOLIAGE_NORMAL_MODE_MASK;
}

float4 UnpackFoliageTint(uint packed)
{
    return float4(
        float(packed & 0xFFu),
        float((packed >> 8u) & 0xFFu),
        float((packed >> 16u) & 0xFFu),
        float((packed >> 24u) & 0xFFu)) / 255.0f;
}

/// A hash in [0, 1) of a world position, quantised to 25 cm so an instance keeps its value as it sways.
float HashFoliagePosition(float3 worldPosition)
{
    int3 cell = int3(floor(worldPosition * 4.0f));
    uint h = asuint(cell.x) * 73856093u ^ asuint(cell.y) * 19349663u ^ asuint(cell.z) * 83492791u;
    h ^= h >> 16u;
    h *= 0x7FEB352Du;
    h ^= h >> 15u;
    h *= 0x846CA68Bu;
    h ^= h >> 16u;
    return float(h & 0x00FFFFFFu) / 16777216.0f;
}

/// The colour a foliage instance's albedo is multiplied by: a tint between healthy and dry, picked by a hash of
/// the instance's world position. White when the material is not foliage or has no tint variation.
float3 EvaluateFoliageTint(GpuMaterialData material, float3 instanceWorldPosition)
{
    float variation = saturate(material.foliageParams.z);
    if (IsMaterialFoliage(material) == false || variation <= 0.0f)
    {
        return float3(1.0f, 1.0f, 1.0f);
    }

    float3 healthy = UnpackFoliageTint(material.foliageHealthyTint).rgb;
    float3 dry = UnpackFoliageTint(material.foliageDryTint).rgb;
    float3 tint = lerp(healthy, dry, HashFoliagePosition(instanceWorldPosition));
    return lerp(float3(1.0f, 1.0f, 1.0f), tint, variation);
}

/// Bends a foliage shading normal toward the material's normal mode. offsetFromBoundsCentre is the surface point
/// relative to the instance's bounds centre, for Spherical. Returns the normal unchanged for Mesh and for other
/// materials.
float3 ApplyFoliageNormalMode(GpuMaterialData material, float3 normal, float3 offsetFromBoundsCentre)
{
    if (IsMaterialFoliage(material) == false)
    {
        return normal;
    }

    uint mode = GetFoliageNormalMode(material);
    float blend = saturate(material.foliageParams.y);
    float3 target = normal;
    if (mode == GPU_MATERIAL_FOLIAGE_NORMAL_SPHERICAL)
    {
        float lengthSquared = dot(offsetFromBoundsCentre, offsetFromBoundsCentre);
        if (lengthSquared > 1e-8f)
        {
            target = offsetFromBoundsCentre * rsqrt(lengthSquared);
        }
    }
    else if (mode == GPU_MATERIAL_FOLIAGE_NORMAL_UP)
    {
        target = float3(0.0f, 1.0f, 0.0f);
    }

    float3 bent = lerp(normal, target, blend);
    float bentLengthSquared = dot(bent, bent);
    return bentLengthSquared > 1e-8f ? bent * rsqrt(bentLengthSquared) : target;
}

/// The normal target's alpha for a material: 1 for a standard surface, base plus translucency for foliage.
float EncodeGBufferShadingModel(GpuMaterialData material)
{
    if (IsMaterialFoliage(material) == false)
    {
        return 1.0f;
    }

    return GBUFFER_FOLIAGE_NORMAL_ALPHA_BASE + saturate(material.foliageParams.x);
}

/// Decodes the normal target's alpha; returns whether the pixel is foliage and, if so, its translucency.
bool DecodeGBufferFoliage(float normalAlpha, out float translucency)
{
    translucency = 0.0f;
    if (normalAlpha < GBUFFER_FOLIAGE_NORMAL_ALPHA_THRESHOLD)
    {
        return false;
    }

    translucency = saturate(normalAlpha - GBUFFER_FOLIAGE_NORMAL_ALPHA_BASE);
    return true;
}
