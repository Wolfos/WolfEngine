import "../Common/draw_data.slang";

// Culls one terrain's placed foliage for up to three views, and expands every visible instance into the instance table's
// foliage region so the geometry shaders draw it like any other instance. See docs/foliage.md.

#define WOLF_MAX_CULL_VIEW_COUNT 3
// Mirrors FrustumCulling.MaxPlaneCount.
#define WOLF_CULL_PLANE_COUNT 24
// Mirrors FoliageTypeRuntime.MaxRendererCount.
#define WOLF_FOLIAGE_MAX_RENDERERS 8
#define WOLF_FOLIAGE_GROUP_SIZE 64
// Groups per dispatch row; chunks past it continue on the next row.
#define WOLF_FOLIAGE_GROUPS_PER_ROW 65535

// Mirrors GpuDrawFlags in GpuDrawData.cs.
static const uint DRAW_KIND_MESH = 0u;

static const uint FOLIAGE_COUNTER_EXPANDED = 0u;
static const uint FOLIAGE_COUNTER_DROPPED = 1u;

cbuffer FoliageCullParams : register(b12)
{
    float4 planes[WOLF_MAX_CULL_VIEW_COUNT * WOLF_CULL_PLANE_COUNT];
    uint planeCounts[WOLF_MAX_CULL_VIEW_COUNT];
    // The terrain's transform; instance positions and cell bounds are in its local space.
    row_major float4x4 localToWorld;
    // x: largest axis scale of localToWorld. y: nonzero for shadow views, which use each type's shadow distance.
    float4 setParams;
    float4 cameraPosition;
    // Mirrors CullParams.lodProjection; levels are measured from the main camera in every view.
    float4 lodProjection;
    float lodHysteresis;
    uint lodEnabled;
    uint lodWriteState;
    uint viewCount;
    uint chunkCount;
    uint participatingLaneMask;
    uint outputDrawArgsStride;
    uint visibleInstanceStride;
    uint maxBatchCount;
    uint expansionBase;
    uint expansionCapacity;
    uint ownerDrawFlags;
};

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

// Mirrors GpuFoliageType in GpuFoliageDraws.cs. lod.levelBatches is unused; levels map to batches per renderer.
struct GpuFoliageType
{
    float4 boundsCenterRadius;
    // x: cull distance, y: shadow distance.
    float4 distances;
    uint rendererStart;
    uint rendererCount;
    uint pad0;
    uint pad1;
    GpuDrawBatchLodData lod;
};

// Mirrors GpuFoliageRenderer in GpuFoliageDraws.cs. levelRange packs the first group level (bits 0-7), the last
// (bits 8-15) and the mesh's level count (bits 16-23). levelBatches[n] draws the mesh's level n; zero for none.
struct GpuFoliageRenderer
{
    row_major float4x4 localTransform;
    uint materialHandle;
    uint meshHandle;
    uint drawFlags;
    uint levelRange;
    uint4 levelBatches[2];
};

RWStructuredBuffer<GpuInstanceData> g_InstanceTable : register(u0);
RWStructuredBuffer<GpuDrawArgs> g_DrawArgs : register(u1);
RWStructuredBuffer<uint> g_VisibleInstances : register(u2);
// The group level the camera cull last picked per instance, kept for hysteresis.
RWStructuredBuffer<uint> g_FoliageLevels : register(u3);
RWStructuredBuffer<uint> g_FoliageCounters : register(u4);
StructuredBuffer<GpuDrawBatchData> g_Batches : register(t5);
StructuredBuffer<uint4> g_FoliageInstances : register(t6);
StructuredBuffer<GpuFoliageChunk> g_FoliageChunks : register(t7);
StructuredBuffer<GpuFoliageType> g_FoliageTypes : register(t8);
StructuredBuffer<GpuFoliageRenderer> g_FoliageRenderers : register(t9);

struct FoliageInstance
{
    float4x4 localToWorld;
    // The largest of the width and height scales, which the type's bounds grow by.
    float maxScale;
    uint typeIndex;
};

float4 UnpackFoliageQuaternion(uint packed)
{
    // Mirrors FoliageInstancePacking.UnpackQuaternion: three 10-bit components in [-1/sqrt(2), 1/sqrt(2)] in 1022 steps,
    // so zero is exact, most significant first, and the index of the left-out largest in the top two bits.
    const float limit = 0.70710678f;
    uint largest = packed >> 30;
    float smallest[3];
    smallest[0] = ((float((packed >> 20) & 0x3FFu) / 1022.0f) * 2.0f - 1.0f) * limit;
    smallest[1] = ((float((packed >> 10) & 0x3FFu) / 1022.0f) * 2.0f - 1.0f) * limit;
    smallest[2] = ((float(packed & 0x3FFu) / 1022.0f) * 2.0f - 1.0f) * limit;
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

// Mirrors FoliageInstancePacking and TerrainFoliageRuntime.GetInstanceTransform: scale, then rotation, then position,
// in the terrain's local space, for row vectors.
FoliageInstance DecodeFoliageInstance(uint4 packed, GpuFoliageChunk chunk)
{
    float3 normalizedPosition = float3(
        float(packed.x & 0xFFFFu),
        float(packed.x >> 16),
        float(packed.y & 0xFFFFu)) / 65535.0f;
    float3 position = lerp(chunk.quantizationMin.xyz, chunk.quantizationMax.xyz, normalizedPosition);
    float heightScale = f16tof32(packed.y >> 16);
    // Zero width bits mean the instance is scaled uniformly.
    uint widthBits = packed.w >> 16;
    float widthScale = widthBits == 0u ? heightScale : f16tof32(widthBits);
    float4 q = UnpackFoliageQuaternion(packed.z);

    float xx = q.x * q.x;
    float yy = q.y * q.y;
    float zz = q.z * q.z;
    float xy = q.x * q.y;
    float xz = q.x * q.z;
    float yz = q.y * q.z;
    float wx = q.w * q.x;
    float wy = q.w * q.y;
    float wz = q.w * q.z;

    // System.Numerics' Matrix4x4.CreateFromQuaternion, row by row.
    float3 row0 = float3(1.0f - 2.0f * (yy + zz), 2.0f * (xy + wz), 2.0f * (xz - wy));
    float3 row1 = float3(2.0f * (xy - wz), 1.0f - 2.0f * (xx + zz), 2.0f * (yz + wx));
    float3 row2 = float3(2.0f * (xz + wy), 2.0f * (yz - wx), 1.0f - 2.0f * (xx + yy));

    FoliageInstance instance;
    instance.localToWorld = float4x4(
        float4(row0 * widthScale, 0.0f),
        float4(row1 * heightScale, 0.0f),
        float4(row2 * widthScale, 0.0f),
        float4(position, 1.0f));
    instance.maxScale = max(abs(widthScale), abs(heightScale));
    instance.typeIndex = packed.w & 0xFFFFu;
    return instance;
}

bool IsSphereVisible(uint viewIndex, float3 center, float radius)
{
    uint planeBase = viewIndex * WOLF_CULL_PLANE_COUNT;
    for (uint i = 0; i < planeCounts[viewIndex]; i++)
    {
        float4 plane = planes[planeBase + i];
        float dist = dot(plane.xyz, center) + plane.w;
        if (dist < -radius)
        {
            return false;
        }
    }

    return true;
}

uint GetVisibleViewMask(float3 relativeCenter, float radius)
{
    uint mask = 0u;
    for (uint viewIndex = 0; viewIndex < viewCount; viewIndex++)
    {
        if (IsSphereVisible(viewIndex, relativeCenter, radius))
        {
            mask |= 1u << viewIndex;
        }
    }

    return mask;
}

uint GetRendererLevelBatch(GpuFoliageRenderer renderer, uint level)
{
    return renderer.levelBatches[level / 4u][level % 4u];
}

// One group per chunk, one thread per instance. Every thread first tests the chunk's bounds, so a chunk that cannot be
// seen costs one test.
[numthreads(WOLF_FOLIAGE_GROUP_SIZE, 1, 1)]
void CSCullFoliage(uint3 groupId : SV_GroupID, uint3 groupThreadId : SV_GroupThreadID)
{
    if (viewCount == 0 || viewCount > WOLF_MAX_CULL_VIEW_COUNT)
    {
        return;
    }

    uint chunkIndex = groupId.y * WOLF_FOLIAGE_GROUPS_PER_ROW + groupId.x;
    if (chunkIndex >= chunkCount)
    {
        return;
    }

    bool isShadowView = setParams.y != 0.0f;
    float setScale = setParams.x;
    GpuFoliageChunk chunk = g_FoliageChunks[chunkIndex];

    // The chunk's box, as a sphere in world space.
    float3 chunkCenter = mul(float4((chunk.cullMin.xyz + chunk.cullMax.xyz) * 0.5f, 1.0f), localToWorld).xyz;
    float chunkRadius = length(chunk.cullMax.xyz - chunk.cullMin.xyz) * 0.5f * setScale;
    float3 chunkRelativeCenter = chunkCenter - cameraPosition.xyz;
    float chunkDistance = isShadowView ? chunk.cullMax.w : chunk.cullMin.w;
    if (length(chunkRelativeCenter) - chunkRadius > chunkDistance ||
        GetVisibleViewMask(chunkRelativeCenter, chunkRadius) == 0u)
    {
        return;
    }

    for (uint local = groupThreadId.x; local < chunk.instanceCount; local += WOLF_FOLIAGE_GROUP_SIZE)
    {
        uint instanceIndex = chunk.instanceStart + local;
        FoliageInstance instance = DecodeFoliageInstance(g_FoliageInstances[instanceIndex], chunk);
        GpuFoliageType type = g_FoliageTypes[instance.typeIndex];
        if (type.rendererCount == 0u)
        {
            continue;
        }

        float4x4 instanceToWorld = mul(instance.localToWorld, localToWorld);
        float3 center = mul(float4(type.boundsCenterRadius.xyz, 1.0f), instanceToWorld).xyz;
        float radius = type.boundsCenterRadius.w * instance.maxScale * setScale;
        float3 relativeCenter = center - cameraPosition.xyz;
        float maxDistance = isShadowView ? type.distances.y : type.distances.x;
        if (length(relativeCenter) - radius > maxDistance)
        {
            continue;
        }

        uint viewMask = GetVisibleViewMask(relativeCenter, radius);
        if (viewMask == 0u)
        {
            continue;
        }

        uint level = 0u;
        if (lodEnabled != 0u && type.lod.levelCount > 1u)
        {
            float screenSize = GetLodScreenSize(relativeCenter, radius, lodProjection);
            uint previousLevel = g_FoliageLevels[instanceIndex];
            level = SelectLodLevel(type.lod, previousLevel, screenSize, lodHysteresis);
            if (lodWriteState != 0u && level != previousLevel)
            {
                g_FoliageLevels[instanceIndex] = level;
            }
        }

        for (uint r = 0; r < type.rendererCount && r < WOLF_FOLIAGE_MAX_RENDERERS; r++)
        {
            GpuFoliageRenderer renderer = g_FoliageRenderers[type.rendererStart + r];
            uint firstLevel = renderer.levelRange & 0xFFu;
            uint lastLevel = (renderer.levelRange >> 8) & 0xFFu;
            uint meshLevelCount = clamp((renderer.levelRange >> 16) & 0xFFu, 1u, GPU_DRAW_MAX_LOD_LEVELS);
            if (level < firstLevel || level > lastLevel)
            {
                continue;
            }

            // A renderer whose mesh has fewer levels than its range keeps drawing its coarsest.
            uint meshLevel = min(level - firstLevel, meshLevelCount - 1u);
            uint batchIndex = GetRendererLevelBatch(renderer, meshLevel);
            if (batchIndex == 0u || batchIndex >= maxBatchCount)
            {
                continue;
            }

            GpuDrawBatchData batch = g_Batches[batchIndex];
            if (batch.capacity == 0u ||
                batch.laneIndex >= 32u ||
                (participatingLaneMask & (1u << batch.laneIndex)) == 0u ||
                batch.visibleOffset + batch.capacity > visibleInstanceStride)
            {
                continue;
            }

            uint expandedSlot;
            InterlockedAdd(g_FoliageCounters[FOLIAGE_COUNTER_EXPANDED], 1u, expandedSlot);
            if (expandedSlot >= expansionCapacity)
            {
                InterlockedAdd(g_FoliageCounters[FOLIAGE_COUNTER_DROPPED], 1u);
                continue;
            }

            float4x4 world = mul(renderer.localTransform, instanceToWorld);
            uint expandedIndex = expansionBase + expandedSlot;
            GpuInstanceData expanded = (GpuInstanceData)0;
            // Placed foliage does not move, so its motion vectors come from the camera alone.
            expanded.previousWorld = world;
            expanded.world = world;
            expanded.boundsCenterRadius = float4(center, radius);
            expanded.materialHandle = renderer.materialHandle;
            expanded.meshHandle = renderer.meshHandle;
            expanded.drawKind = DRAW_KIND_MESH;
            expanded.flags = renderer.drawFlags | ownerDrawFlags;
            g_InstanceTable[expandedIndex] = expanded;

            for (uint viewIndex = 0; viewIndex < viewCount; viewIndex++)
            {
                if ((viewMask & (1u << viewIndex)) == 0u)
                {
                    continue;
                }

                uint argsIndex = (viewIndex * outputDrawArgsStride) + batchIndex;
                uint slot;
                InterlockedAdd(g_DrawArgs[argsIndex].instanceCount, 1u, slot);
                // Reservations and the batch table are written in the same frame, so a slot past capacity means they
                // disagree; it would belong to the next batch, so it is dropped.
                if (slot >= batch.capacity)
                {
                    continue;
                }

                g_VisibleInstances[(viewIndex * visibleInstanceStride) + batch.visibleOffset + slot] = expandedIndex;
            }
        }
    }
}
