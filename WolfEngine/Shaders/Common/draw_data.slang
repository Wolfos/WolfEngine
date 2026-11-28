// GPU draw data layouts for bindless/indirect rendering.

// Mirrors GpuDrawHandle in GpuDrawHandles.cs: a 20-bit index and a 12-bit generation.
static const uint WOLF_HANDLE_INDEX_MASK = 0xFFFFF;
static const uint WOLF_HANDLE_GEN_SHIFT = 20;

uint GetHandleIndex(uint handle)
{
    return handle & WOLF_HANDLE_INDEX_MASK;
}

uint GetHandleGeneration(uint handle)
{
    return handle >> WOLF_HANDLE_GEN_SHIFT;
}

bool IsPackedHandleValid(uint handle)
{
    return handle != 0 && GetHandleGeneration(handle) != 0;
}

struct GpuInstanceData
{
    row_major float4x4 previousWorld;
    row_major float4x4 world;
    float4 boundsCenterRadius;
    float4 terrainChunkOriginSize;
    float4 terrainHeightmapUvScaleOffset;
    uint materialHandle;
    uint meshHandle;
    uint drawKind;
    uint flags;
    // A LodGroup's bounds in world space, which a grouped instance's level is measured on.
    float4 lodSphere;
    // Packed as below; zero for an instance outside any group. See docs/mesh-lod.md.
    uint lodParams;
    uint lodPad0;
    uint lodPad1;
    uint lodPad2;
};

// Mirrors GpuDrawLodParams in GpuDrawData.cs.
static const uint LOD_PARAMS_GROUPED = 1u;
static const uint LOD_PARAMS_FIRST_LEVEL_SHIFT = 1u;
static const uint LOD_PARAMS_LAST_LEVEL_SHIFT = 4u;
static const uint LOD_PARAMS_THRESHOLD_SET_SHIFT = 7u;
static const uint LOD_PARAMS_LEVEL_MASK = 0x7u;

// Bits of GpuMaterialData.materialFlags. Mirrors GpuMaterialFlags in GpuDrawData.cs.
static const uint GPU_MATERIAL_FLAG_DOUBLE_SIDED = 1u << 0;
static const uint GPU_MATERIAL_FLAG_FOLIAGE = 1u << 1;
static const uint GPU_MATERIAL_FOLIAGE_NORMAL_MODE_SHIFT = 2u;
static const uint GPU_MATERIAL_FOLIAGE_NORMAL_MODE_MASK = 0x3u;
// Mirrors FoliageNormalMode.
static const uint GPU_MATERIAL_FOLIAGE_NORMAL_MESH = 0u;
static const uint GPU_MATERIAL_FOLIAGE_NORMAL_SPHERICAL = 1u;
static const uint GPU_MATERIAL_FOLIAGE_NORMAL_UP = 2u;

// metallicRoughness is (metallic, roughness, alpha cutoff, normal scale). The cutoff is zero for materials
// that are not alpha tested.
struct GpuMaterialData
{
    float4 baseColor;
    float4 metallicRoughness;
    float4 emissiveFactorIntensity;
    float4 uvOffsetScale;
    uint albedoHandle;
    uint ormHandle;
    uint normalHandle;
    uint emissiveHandle;
    uint samplerHandle;
    uint materialFlags;
    // Foliage only, zero otherwise: RGBA8 unorm tints, and (translucency, normal blend, tint variation, 0).
    uint foliageHealthyTint;
    uint foliageDryTint;
    float4 foliageParams;
    // WIND_MODE_ in wind.slang; zero for no wind.
    uint windMode;
    // Strength scale and flutter scale, as two halves.
    uint windScales;
    uint windPad0;
    uint windPad1;
};

bool IsMaterialDoubleSided(GpuMaterialData material)
{
    return (material.materialFlags & GPU_MATERIAL_FLAG_DOUBLE_SIDED) != 0u;
}

struct GpuTerrainMaterialData
{
    uint heightmapHandle;
    uint layerIndexMapHandle;
    uint layerWeightMapHandle;
    uint hasLayerMaps;
    uint heightmapSamplerHandle;
    uint layerSamplerHandle;
    uint layerMapSamplerHandle;
    uint layerStart;
    uint layerCount;
    float heightBlendSharpness;
    float heightScale;
    float autoMaterialBlendDegrees;
    uint pad0;
    uint pad1;
    uint pad2;
    uint pad3;
};

struct GpuTerrainLayerData
{
    uint albedoHandle;
    uint normalHandle;
    uint ormHandle;
    uint heightHandle;
    uint hasHeight;
    float scale;
    uint autoMaterial;
    uint useMinimumSlope;
    float minimumSlopeDegrees;
    uint hasNormal;
    uint hasOrm;
    uint pad2;
};

struct GpuMeshData
{
    uint vertexBufferHandle;
    uint indexBufferHandle;
    uint indexCount;
    uint indexFormat;
    uint baseVertex;
    uint startIndex;
    uint pad1;
    uint pad2;
};

// Indexed by instance index. batchIndex names the batch whose visible list culling appends the instance to: its
// level 0 batch when its mesh has levels of detail. lodLevel is the level the camera cull last picked, kept for
// hysteresis; see docs/mesh-lod.md.
struct GpuDrawCommand
{
    uint instanceHandle;
    uint batchIndex;
    uint flags;
    uint lodLevel;
};

// Indexed by batch index, which is also the draw index of the batch's records. A batch's visible instances live
// at [visibleOffset, visibleOffset + capacity) of each view's slice of the visible instance list. Capacity is
// zero for a batch with no members.
struct GpuDrawBatchData
{
    uint visibleOffset;
    uint capacity;
    uint laneIndex;
    uint pad0;
};

// Mirrors GpuDrawBatchLodData in GpuDrawData.cs. levelBatches[n] draws level n; screenSizes[n] is the threshold
// into level n + 1. Eight levels, packed four to a vector.
static const uint GPU_DRAW_MAX_LOD_LEVELS = 8;

struct GpuDrawBatchLodData
{
    uint levelCount;
    uint pad0;
    uint pad1;
    uint pad2;
    uint4 levelBatches[2];
    float4 screenSizes[2];
};

uint GetLodLevelBatch(GpuDrawBatchLodData lod, uint level)
{
    return lod.levelBatches[level / 4u][level % 4u];
}

float GetLodThreshold(GpuDrawBatchLodData lod, uint index)
{
    return lod.screenSizes[index / 4u][index % 4u];
}

// The fraction of the screen height a sphere's diameter covers, scaled by the bias. lodProjection is (projection
// vertical scale, clip W per unit distance, clip W at zero distance, bias), measured from the main camera.
float GetLodScreenSize(float3 relativeCenter, float radius, float4 lodProjection)
{
    float clipW = max(length(relativeCenter) * lodProjection.y + lodProjection.z, 1e-4f);
    return radius * lodProjection.x / clipW * lodProjection.w;
}

// Steps from the level an instance had towards the one its screen size asks for. A level changes only once the size
// passes its threshold by the hysteresis margin, so an object resting on a threshold keeps its level. Gives the same
// answer when fed its own result, which lets shadow culls repeat the camera's choice without storing it.
uint SelectLodLevel(GpuDrawBatchLodData lod, uint previousLevel, float screenSize, float hysteresis)
{
    uint level = min(previousLevel, lod.levelCount - 1u);
    while (level + 1u < lod.levelCount &&
           screenSize < GetLodThreshold(lod, level) * (1.0f - hysteresis))
    {
        level++;
    }

    while (level > 0u &&
           screenSize > GetLodThreshold(lod, level - 1u) * (1.0f + hysteresis))
    {
        level--;
    }

    return level;
}

struct GpuDrawArgs
{
    uint indexCount;
    uint instanceCount;
    uint startIndex;
    int baseVertex;
    uint startInstance;
    uint pad0;
    uint pad1;
    uint pad2;
};

struct GpuDrawInstanceUpdateData
{
    row_major float4x4 previousWorld;
    row_major float4x4 world;
    float4 boundsCenterRadius;
    float4 terrainChunkOriginSize;
    float4 terrainHeightmapUvScaleOffset;
    uint32_t type;
    uint32_t batchIndex;
    uint32_t instanceHandle;
    uint32_t drawKind;
    uint32_t meshHandle;
    uint32_t materialHandle;
    uint32_t drawFlags;
    uint32_t pad0;
    float4 lodSphere;
    uint32_t lodParams;
    uint32_t lodPad0;
    uint32_t lodPad1;
    uint32_t lodPad2;
};

struct GpuDrawMeshUpdateData
{
    uint32_t meshHandle;
    uint32_t vertexBufferHandle;
    uint32_t indexBufferHandle;
    uint32_t indexCount;
    uint32_t indexFormat;
    int32_t baseVertex;
    uint32_t startIndex;
    uint32_t pad1;
};

struct GpuDrawMaterialUpdateData
{
    uint32_t materialHandle;
    uint32_t pad0;
    uint32_t pad1;
    uint32_t pad2;
    float4 baseColor;
    float4 metallicRoughness;
    float4 emissiveFactorIntensity;
    float4 uvOffsetScale;
    uint32_t albedoHandle;
    uint32_t ormHandle;
    uint32_t normalHandle;
    uint32_t emissiveHandle;
    uint32_t samplerHandle;
    uint32_t materialFlags;
    uint32_t foliageHealthyTint;
    uint32_t foliageDryTint;
    float4 foliageParams;
    uint32_t windMode;
    uint32_t windScales;
    uint32_t windPad0;
    uint32_t windPad1;
};

struct GpuTerrainMaterialUpdateData
{
    uint32_t materialHandle;
    uint32_t heightmapHandle;
    uint32_t layerIndexMapHandle;
    uint32_t layerWeightMapHandle;
    uint32_t hasLayerMaps;
    uint32_t heightmapSamplerHandle;
    uint32_t layerSamplerHandle;
    uint32_t layerMapSamplerHandle;
    uint32_t layerStart;
    uint32_t layerCount;
    float heightBlendSharpness;
    float heightScale;
    float autoMaterialBlendDegrees;
    uint32_t pad0;
    uint32_t pad1;
    uint32_t pad2;
};

struct GpuTerrainLayerUpdateData
{
    uint32_t materialHandle;
    uint32_t layerStart;
    uint32_t layerIndex;
    uint32_t albedoHandle;
    uint32_t normalHandle;
    uint32_t ormHandle;
    uint32_t heightHandle;
    uint32_t hasHeight;
    float scale;
    uint32_t autoMaterial;
    uint32_t useMinimumSlope;
    float minimumSlopeDegrees;
    uint32_t hasNormal;
    uint32_t hasOrm;
    uint32_t pad2;
    uint32_t pad3;
};

struct GpuDecalProjectorData
{
    row_major float4x4 localToWorld;
    row_major float4x4 worldToLocal;
    float4 uvScaleOffset;
    float4 tint;
    float4 opacities;
    float4 materialFactorsEmissive;
    uint channelMask;
    uint albedoHandle;
    uint normalHandle;
    uint materialHandle;
    uint emissiveHandle;
    uint samplerHandle;
    uint pad0;
    uint pad1;
    uint pad2;
};
