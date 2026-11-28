import "../Common/common_bindless.slang";
import "../Common/draw_data.slang";
import "../Terrain/terrain_height_sampling.slang";
import "../Terrain/terrain_layer_blend.slang";
import "foliage_common.slang";

// Generates one detail type's plants around the camera into the chunks the foliage cull reads, in the packed form placed
// foliage is stored in. One thread group per tile of the window around the camera, one thread per candidate. See
// docs/foliage.md and TerrainDetailRuntime.cs.
//
// Candidates sit one per cell of a grid in the terrain's local space, jittered inside the cell, with random values
// derived from the cell alone. A candidate is kept while its random value is below the density there, as a fraction of
// the grid's density, so raising density only adds plants, lowering it only removes them, and nothing moves.

#define WOLF_DETAIL_TILE_AXIS 8
#define WOLF_DETAIL_GROUP_SIZE 64
// Groups per dispatch row; tiles past it continue on the next row.
#define WOLF_DETAIL_GROUPS_PER_ROW 65535

static const float DETAIL_PI = 3.14159265f;

// Of the density at a candidate, the fraction below its random value over which a plant near the edge of the
// distance shrinks to nothing before it is dropped, so plants fade out instead of popping.
static const float DETAIL_FADE_SHRINK_BAND = 0.35f;

cbuffer DetailGenerateParams : register(b12)
{
    // The main camera, in the terrain's local space.
    float4 localCamera;
    // x, y: the terrain's size along local X and Z. z: its height scale. w: largest axis scale of its transform.
    float4 terrainSize;
    // The type's bounds in its own space: centre and radius.
    float4 typeBounds;
    // x: candidate spacing. y: tile size. z: distance. w: fade distance; all but spacing in world metres.
    float4 grid;
    int windowOriginX;
    int windowOriginZ;
    uint gridDimension;
    uint chunkStart;
    float maxDensity;
    float densityScale;
    float shadowDistance;
    uint typeIndex;
    uint typeCount;
    uint layerCount;
    uint seed;
    uint terrainMaterialHandle;
};

// Mirrors GpuDetailEntry in TerrainDetailRuntime.cs.
struct GpuDetailEntry
{
    // x: density per square metre, y: min height scale, z: max height scale, w: align to normal.
    float4 densityScale;
    // x: min width ratio, y: max width ratio, z: clump size in metres (zero for none), w: clump strength.
    float4 widthClump;
    // x: steepest slope in degrees, y: lowest height, z: highest height.
    float4 limits;
};

RWStructuredBuffer<uint4> g_DetailInstances : register(u0);
RWStructuredBuffer<GpuFoliageChunk> g_DetailChunks : register(u1);
StructuredBuffer<GpuTerrainMaterialData> g_TerrainMaterialTable : register(t2);
StructuredBuffer<GpuTerrainLayerData> g_TerrainLayerTable : register(t3);
StructuredBuffer<GpuDetailEntry> g_DetailEntries : register(t4);

groupshared float gs_MinHeight[WOLF_DETAIL_GROUP_SIZE];
groupshared float gs_MaxHeight[WOLF_DETAIL_GROUP_SIZE];
groupshared float gs_MaxScale[WOLF_DETAIL_GROUP_SIZE];

uint DetailHash(uint value)
{
    // PCG, from Jarzynski and Olano, "Hash Functions for GPU Rendering", JCGT 2020.
    uint state = value * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

uint DetailHashCell(int2 cell, uint salt)
{
    return DetailHash(asuint(cell.x) ^ DetailHash(asuint(cell.y) ^ DetailHash(salt)));
}

float DetailHashToUnit(uint hash)
{
    return float(hash >> 8) / 16777216.0f;
}

float DetailValueNoise(float2 position, uint salt)
{
    float2 cellFloor = floor(position);
    int2 cell = int2(cellFloor);
    float2 fraction = position - cellFloor;
    float2 blend = fraction * fraction * (3.0f - 2.0f * fraction);
    float a = DetailHashToUnit(DetailHashCell(cell, salt));
    float b = DetailHashToUnit(DetailHashCell(cell + int2(1, 0), salt));
    float c = DetailHashToUnit(DetailHashCell(cell + int2(0, 1), salt));
    float d = DetailHashToUnit(DetailHashCell(cell + int2(1, 1), salt));
    return lerp(lerp(a, b, blend.x), lerp(c, d, blend.x), blend.y);
}

// Two octaves, so patches have ragged edges instead of the value noise's grid.
float DetailClumpNoise(float2 position, uint salt)
{
    float coarse = DetailValueNoise(position, salt);
    float fine = DetailValueNoise(position * 2.37f + 17.0f, salt ^ 0x5bd1e995u);
    return saturate(coarse * 0.65f + fine * 0.35f);
}

float3 ComputeDetailTerrainNormal(Texture2D<float4> heightmap, uint width, uint height, float2 uv, float heightScale)
{
    float2 texel = float2(
        width > 1 ? 1.0f / (width - 1) : 0.0f,
        height > 1 ? 1.0f / (height - 1) : 0.0f);
    float left = SampleTerrainHeightBicubic(heightmap, width, height, uv - float2(texel.x, 0.0f), heightScale);
    float right = SampleTerrainHeightBicubic(heightmap, width, height, uv + float2(texel.x, 0.0f), heightScale);
    float down = SampleTerrainHeightBicubic(heightmap, width, height, uv - float2(0.0f, texel.y), heightScale);
    float up = SampleTerrainHeightBicubic(heightmap, width, height, uv + float2(0.0f, texel.y), heightScale);
    float spacingX = terrainSize.x / max(width - 1, 1);
    float spacingZ = terrainSize.y / max(height - 1, 1);
    float3 tangentX = float3(max(spacingX * 2.0f, 0.001f), right - left, 0.0f);
    float3 tangentZ = float3(0.0f, up - down, max(spacingZ * 2.0f, 0.001f));
    return normalize(cross(tangentZ, tangentX));
}

// Hamilton product: the rotation b, then a.
float4 MultiplyQuaternions(float4 a, float4 b)
{
    return float4(
        a.w * b.xyz + b.w * a.xyz + cross(a.xyz, b.xyz),
        a.w * b.w - dot(a.xyz, b.xyz));
}

// The shortest rotation taking +Y to the unit vector to.
float4 RotationFromUp(float3 to)
{
    float3 up = float3(0.0f, 1.0f, 0.0f);
    float4 q = float4(cross(up, to), 1.0f + dot(up, to));
    return normalize(q);
}

uint QuantizeDetailUnorm16(float value, float minimum, float maximum)
{
    float extent = maximum - minimum;
    if (extent <= 0.0f)
    {
        return 0u;
    }

    return uint(round(saturate((value - minimum) / extent) * 65535.0f));
}

struct DetailPlant
{
    bool kept;
    float3 position;
    float4 rotation;
    float heightScale;
    float widthScale;
};

DetailPlant EvaluateCandidate(int2 cell)
{
    DetailPlant plant;
    plant.kept = false;
    plant.position = 0.0f.xxx;
    plant.rotation = float4(0.0f, 0.0f, 0.0f, 1.0f);
    plant.heightScale = 0.0f;
    plant.widthScale = 0.0f;

    uint hash0 = DetailHashCell(cell, seed);
    uint hash1 = DetailHash(hash0);
    uint hash2 = DetailHash(hash1);
    uint hash3 = DetailHash(hash2);
    uint hash4 = DetailHash(hash3);
    uint hash5 = DetailHash(hash4);

    float spacing = grid.x;
    float2 local = (float2(cell) + float2(DetailHashToUnit(hash0), DetailHashToUnit(hash1))) * spacing;
    float2 uv = local / terrainSize.xy + 0.5f;
    if (any(uv < 0.0f) || any(uv > 1.0f))
    {
        return plant;
    }

    if (IsPackedHandleValid(terrainMaterialHandle) == false)
    {
        return plant;
    }

    GpuTerrainMaterialData terrainData = g_TerrainMaterialTable[GetHandleIndex(terrainMaterialHandle)];
    Texture2D<float4> heightmap = GetTexture(terrainData.heightmapHandle);
    uint heightmapWidth = 1;
    uint heightmapHeight = 1;
    heightmap.GetDimensions(heightmapWidth, heightmapHeight);
    float terrainHeight = SampleTerrainHeightBicubic(heightmap, heightmapWidth, heightmapHeight, uv, terrainData.heightScale);
    float3 position = float3(local.x, terrainHeight, local.y);

    // Distance thinning, measured in world metres as the cull measures its distances.
    float distance = length(position - localCamera.xyz) * terrainSize.w;
    float fadeDistance = max(grid.w, 1e-3f);
    float remaining = saturate((grid.z - distance) / fadeDistance);
    if (remaining <= 0.0f)
    {
        return plant;
    }

    float3 normal = ComputeDetailTerrainNormal(heightmap, heightmapWidth, heightmapHeight, uv, terrainData.heightScale);
    float slopeDegrees = degrees(acos(saturate(normal.y)));
    TerrainLayerBlend blend = SampleTerrainLayerBlend(
        GetTexture(terrainData.layerIndexMapHandle),
        GetTexture(terrainData.layerWeightMapHandle),
        g_TerrainLayerTable,
        uv,
        normal,
        terrainData);
    float4 weights = NormalizeTerrainLayerWeights(blend.weights);

    // Density blends by layer weight; the rest of the entry comes from the layer contributing the most.
    float density = 0.0f;
    float strongest = 0.0f;
    GpuDetailEntry growth = (GpuDetailEntry)0;
    [unroll]
    for (uint slot = 0u; slot < 4u; slot++)
    {
        uint layerIndex = blend.indices[slot];
        if (weights[slot] <= 0.0f || layerIndex >= layerCount)
        {
            continue;
        }

        GpuDetailEntry entry = g_DetailEntries[layerIndex * typeCount + typeIndex];
        if (entry.densityScale.x <= 0.0f ||
            slopeDegrees > entry.limits.x ||
            terrainHeight < entry.limits.y ||
            terrainHeight > entry.limits.z)
        {
            continue;
        }

        float contribution = weights[slot] * entry.densityScale.x;
        density += contribution;
        if (contribution > strongest)
        {
            strongest = contribution;
            growth = entry;
        }
    }

    if (density <= 0.0f)
    {
        return plant;
    }

    if (growth.widthClump.z > 0.0f && growth.widthClump.w > 0.0f)
    {
        float noise = DetailClumpNoise(local / growth.widthClump.z, seed ^ 0x27d4eb2du);
        density *= saturate(1.0f - growth.widthClump.w + growth.widthClump.w * 2.0f * noise);
    }

    float probability = saturate((density / max(maxDensity, 1e-6f)) * densityScale);
    float threshold = probability * remaining;
    float randomValue = DetailHashToUnit(hash2);
    if (randomValue >= threshold)
    {
        return plant;
    }

    // Within the fade, a plant shrinks as the threshold closes in on its random value and is gone when it passes it.
    float shrink = 1.0f;
    float band = DETAIL_FADE_SHRINK_BAND * probability * (1.0f - remaining);
    if (band > 0.0f)
    {
        shrink = saturate((threshold - randomValue) / band);
    }

    float heightScale = lerp(growth.densityScale.y, growth.densityScale.z, DetailHashToUnit(hash3)) * shrink;
    float widthScale = heightScale * lerp(growth.widthClump.x, growth.widthClump.y, DetailHashToUnit(hash4));
    if (heightScale <= 1e-3f)
    {
        return plant;
    }

    float yaw = DetailHashToUnit(hash5) * 2.0f * DETAIL_PI;
    float4 yawRotation = float4(0.0f, sin(yaw * 0.5f), 0.0f, cos(yaw * 0.5f));
    float3 up = normalize(lerp(float3(0.0f, 1.0f, 0.0f), normal, growth.densityScale.w));

    plant.kept = true;
    plant.position = position;
    plant.rotation = MultiplyQuaternions(RotationFromUp(up), yawRotation);
    plant.heightScale = heightScale;
    plant.widthScale = widthScale;
    return plant;
}

[numthreads(WOLF_DETAIL_GROUP_SIZE, 1, 1)]
void CSGenerateDetail(uint3 groupId : SV_GroupID, uint3 groupThreadId : SV_GroupThreadID)
{
    uint slot = groupId.y * WOLF_DETAIL_GROUPS_PER_ROW + groupId.x;
    uint slotCount = gridDimension * gridDimension;
    if (slot >= slotCount)
    {
        return;
    }

    // The window's tiles map onto slots modulo the grid dimension; this slot holds the one window tile congruent to it.
    // Mirrors TerrainDetailRuntime.GetTileSlot.
    int dimension = int(gridDimension);
    int slotX = int(slot % gridDimension);
    int slotZ = int(slot / gridDimension);
    int tileX = windowOriginX + ((((slotX - windowOriginX) % dimension) + dimension) % dimension);
    int tileZ = windowOriginZ + ((((slotZ - windowOriginZ) % dimension) + dimension) % dimension);

    uint thread = groupThreadId.x;
    int2 cell = int2(
        tileX * WOLF_DETAIL_TILE_AXIS + int(thread % WOLF_DETAIL_TILE_AXIS),
        tileZ * WOLF_DETAIL_TILE_AXIS + int(thread / WOLF_DETAIL_TILE_AXIS));
    DetailPlant plant = EvaluateCandidate(cell);

    float tileSize = grid.y;
    float2 tileMin = float2(tileX, tileZ) * tileSize;
    float2 tileMax = tileMin + tileSize;
    float heightScaleMax = max(terrainSize.z, 1e-3f);

    uint chunkIndex = chunkStart + slot;
    uint instanceIndex = chunkIndex * WOLF_DETAIL_GROUP_SIZE + thread;
    uint4 packed = uint4(0u, 0u, 0u, FOLIAGE_EMPTY_TYPE_INDEX);
    if (plant.kept)
    {
        uint x = QuantizeDetailUnorm16(plant.position.x, tileMin.x, tileMax.x);
        uint y = QuantizeDetailUnorm16(plant.position.y, 0.0f, heightScaleMax);
        uint z = QuantizeDetailUnorm16(plant.position.z, tileMin.y, tileMax.y);
        uint heightBits = f32tof16(plant.heightScale);
        uint widthBits = f32tof16(plant.widthScale);
        // Zero width bits mean the same as the height scale, as FoliageInstancePacking writes uniform instances.
        widthBits = widthBits == heightBits ? 0u : widthBits;
        packed = uint4(
            x | (y << 16),
            z | (heightBits << 16),
            PackFoliageQuaternion(plant.rotation),
            typeIndex | (widthBits << 16));
    }

    g_DetailInstances[instanceIndex] = packed;

    gs_MinHeight[thread] = plant.kept ? plant.position.y : 3.0e38f;
    gs_MaxHeight[thread] = plant.kept ? plant.position.y : -3.0e38f;
    gs_MaxScale[thread] = plant.kept ? max(plant.heightScale, plant.widthScale) : 0.0f;
    GroupMemoryBarrierWithGroupSync();

    for (uint stride = WOLF_DETAIL_GROUP_SIZE / 2u; stride > 0u; stride >>= 1u)
    {
        if (thread < stride)
        {
            gs_MinHeight[thread] = min(gs_MinHeight[thread], gs_MinHeight[thread + stride]);
            gs_MaxHeight[thread] = max(gs_MaxHeight[thread], gs_MaxHeight[thread + stride]);
            gs_MaxScale[thread] = max(gs_MaxScale[thread], gs_MaxScale[thread + stride]);
        }

        GroupMemoryBarrierWithGroupSync();
    }

    if (thread != 0u)
    {
        return;
    }

    GpuFoliageChunk chunk = (GpuFoliageChunk)0;
    chunk.quantizationMin = float4(tileMin.x, 0.0f, tileMin.y, 0.0f);
    chunk.quantizationMax = float4(tileMax.x, heightScaleMax, tileMax.y, 0.0f);
    chunk.instanceStart = chunkIndex * WOLF_DETAIL_GROUP_SIZE;
    chunk.instanceCount = 0u;
    if (gs_MaxScale[0] > 0.0f)
    {
        // Every plant's bounds fit in the tile grown by the type's reach at the largest scale in it.
        float reach = (length(typeBounds.xyz) + typeBounds.w) * gs_MaxScale[0];
        chunk.cullMin = float4(tileMin.x - reach, gs_MinHeight[0] - reach, tileMin.y - reach, grid.z);
        chunk.cullMax = float4(tileMax.x + reach, gs_MaxHeight[0] + reach, tileMax.y + reach, shadowDistance);
        chunk.instanceCount = WOLF_DETAIL_GROUP_SIZE;
    }

    g_DetailChunks[chunkIndex] = chunk;
}
