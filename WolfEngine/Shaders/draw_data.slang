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
    uint metallicRoughnessHandle;
    uint normalHandle;
    uint occlusionHandle;
    uint emissiveHandle;
    uint samplerHandle;
    uint pad0;
    uint pad1;
};

struct GpuTerrainMaterialData
{
    uint controlMapHandle;
    uint hasControlMap;
    uint layerSamplerHandle;
    uint controlSamplerHandle;
    uint layerStart;
    uint layerCount;
    float heightBlendSharpness;
    uint pad0;
    uint pad1;
    uint pad2;
    uint pad3;
    uint pad4;
};

struct GpuTerrainLayerData
{
    uint albedoHandle;
    uint normalHandle;
    uint metallicRoughnessHandle;
    uint occlusionHandle;
    uint heightHandle;
    uint hasHeight;
    float scale;
    uint pad0;
};

struct GpuMeshData
{
    uint vertexBufferHandle;
    uint indexBufferHandle;
    uint indexCount;
    uint indexFormat;
    uint baseVertex;
    uint pad0;
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
    uint32_t pad0;
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
    uint32_t metallicRoughnessHandle;
    uint32_t normalHandle;
    uint32_t occlusionHandle;
    uint32_t emissiveHandle;
    uint32_t samplerHandle;
    uint32_t pad3;
    uint32_t pad4;
};

struct GpuTerrainMaterialUpdateData
{
    uint32_t materialHandle;
    uint32_t controlMapHandle;
    uint32_t hasControlMap;
    uint32_t layerSamplerHandle;
    uint32_t controlSamplerHandle;
    uint32_t layerStart;
    uint32_t layerCount;
    float heightBlendSharpness;
};

struct GpuTerrainLayerUpdateData
{
    uint32_t materialHandle;
    uint32_t layerStart;
    uint32_t layerIndex;
    uint32_t albedoHandle;
    uint32_t normalHandle;
    uint32_t metallicRoughnessHandle;
    uint32_t occlusionHandle;
    uint32_t heightHandle;
    uint32_t hasHeight;
    float scale;
    uint32_t pad0;
    uint32_t pad1;
};
