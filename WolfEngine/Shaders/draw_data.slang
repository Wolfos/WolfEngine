// GPU draw data layouts for bindless/indirect rendering.

struct GpuInstanceData
{
    row_major float4x4 world;
    float4 boundsCenterRadius;
    uint materialId;
    uint meshId;
    uint flags;
    uint pad0;
};

struct GpuMaterialData
{
    float4 baseColor;
    float4 metallicRoughness;
    uint albedoHandle;
    uint metallicRoughnessHandle;
    uint normalHandle;
    uint occlusionHandle;
    uint emissiveHandle;
    uint samplerHandle;
    uint pad0;
    uint pad1;
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
    uint instanceId;
    uint drawId;
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
};

struct GpuDrawUpdateData
{
    row_major float4x4 world;
    float4 boundsCenterRadius;
    float4 baseColor;
    float4 metallicRoughness;
    uint32_t type;
    uint32_t drawId;
    uint32_t instanceId;
    uint32_t meshId;
    uint32_t materialId;
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
    uint32_t pad0;
    uint32_t pad1;
    uint32_t pad2;
};
