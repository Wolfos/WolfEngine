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
    uint layerCount;
    float heightBlendSharpness;
    uint pad0;
    uint pad1;
    uint layer0AlbedoHandle;
    uint layer0NormalHandle;
    uint layer0MetallicRoughnessHandle;
    uint layer0OcclusionHandle;
    uint layer0HeightHandle;
    uint layer0HasHeight;
    float layer0Scale;
    uint layer0Pad;
    uint layer1AlbedoHandle;
    uint layer1NormalHandle;
    uint layer1MetallicRoughnessHandle;
    uint layer1OcclusionHandle;
    uint layer1HeightHandle;
    uint layer1HasHeight;
    float layer1Scale;
    uint layer1Pad;
    uint layer2AlbedoHandle;
    uint layer2NormalHandle;
    uint layer2MetallicRoughnessHandle;
    uint layer2OcclusionHandle;
    uint layer2HeightHandle;
    uint layer2HasHeight;
    float layer2Scale;
    uint layer2Pad;
    uint layer3AlbedoHandle;
    uint layer3NormalHandle;
    uint layer3MetallicRoughnessHandle;
    uint layer3OcclusionHandle;
    uint layer3HeightHandle;
    uint layer3HasHeight;
    float layer3Scale;
    uint layer3Pad;
    uint pad2;
    uint pad3;
    uint pad4;
    uint pad5;
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

struct GpuDrawUpdateData
{
    row_major float4x4 previousWorld;
    row_major float4x4 world;
    float4 boundsCenterRadius;
    float4 baseColor;
    float4 metallicRoughness;
    float4 emissiveFactorIntensity;
    uint32_t type;
    uint32_t drawHandle;
    uint32_t instanceHandle;
    uint32_t drawKind;
    uint32_t meshHandle;
    uint32_t materialHandle;
    uint32_t drawFlags;
    uint32_t vertexBufferHandle;
    uint32_t indexBufferHandle;
    uint32_t indexCount;
    uint32_t indexFormat;
    int32_t baseVertex;
    uint32_t albedoHandle;
    uint32_t metallicRoughnessHandle;
    uint32_t normalHandle;
    uint32_t occlusionHandle;
    uint32_t emissiveHandle;
    uint32_t samplerHandle;
    uint32_t controlMapHandle;
    uint32_t hasControlMap;
    uint32_t layerSamplerHandle;
    uint32_t controlSamplerHandle;
    uint32_t layerCount;
    float heightBlendSharpness;
    uint32_t pad0;
    uint32_t pad1;
    uint32_t layer0AlbedoHandle;
    uint32_t layer0NormalHandle;
    uint32_t layer0MetallicRoughnessHandle;
    uint32_t layer0OcclusionHandle;
    uint32_t layer0HeightHandle;
    uint32_t layer0HasHeight;
    float layer0Scale;
    uint32_t layer0Pad;
    uint32_t layer1AlbedoHandle;
    uint32_t layer1NormalHandle;
    uint32_t layer1MetallicRoughnessHandle;
    uint32_t layer1OcclusionHandle;
    uint32_t layer1HeightHandle;
    uint32_t layer1HasHeight;
    float layer1Scale;
    uint32_t layer1Pad;
    uint32_t layer2AlbedoHandle;
    uint32_t layer2NormalHandle;
    uint32_t layer2MetallicRoughnessHandle;
    uint32_t layer2OcclusionHandle;
    uint32_t layer2HeightHandle;
    uint32_t layer2HasHeight;
    float layer2Scale;
    uint32_t layer2Pad;
    uint32_t layer3AlbedoHandle;
    uint32_t layer3NormalHandle;
    uint32_t layer3MetallicRoughnessHandle;
    uint32_t layer3OcclusionHandle;
    uint32_t layer3HeightHandle;
    uint32_t layer3HasHeight;
    float layer3Scale;
    uint32_t layer3Pad;
    uint32_t pad2;
    uint32_t pad3;
};
