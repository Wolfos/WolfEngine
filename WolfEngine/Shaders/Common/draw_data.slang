// GPU draw data layouts for bindless/indirect rendering.

static const uint WOLF_HANDLE_INDEX_MASK = 0xFFFF;
static const uint WOLF_HANDLE_GEN_SHIFT = 16;

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
};

struct GpuMaterialData
{
    float4 baseColor;
    float4 metallicRoughness;
    float4 emissiveFactorIntensity;
    uint albedoHandle;
    uint ormHandle;
    uint normalHandle;
    uint emissiveHandle;
    uint samplerHandle;
    uint pad0;
    uint pad1;
    uint pad2;
};

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

struct GpuDrawCommand
{
    uint instanceHandle;
    uint drawHandle;
    uint flags;
    uint pad0;
};

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
    uint32_t drawHandle;
    uint32_t instanceHandle;
    uint32_t drawKind;
    uint32_t meshHandle;
    uint32_t materialHandle;
    uint32_t drawFlags;
    uint32_t pad0;
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
    uint32_t albedoHandle;
    uint32_t ormHandle;
    uint32_t normalHandle;
    uint32_t emissiveHandle;
    uint32_t samplerHandle;
    uint32_t pad3;
    uint32_t pad4;
    uint32_t pad5;
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
