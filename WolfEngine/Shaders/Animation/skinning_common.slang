// Shared linear-blend skinning for current geometry and previous-frame motion vectors.

struct GpuBoneMatrix
{
    row_major float4x4 skin;
};

// Float4 bindPosition keeps this shared struct's stride backend-independent.
struct GpuSkinVertex
{
    float4 bindPosition;
    uint4 boneIndices;
    float4 boneWeights;
};

// Indexed by mesh-handle slot; meshHandle validates recycled entries.
struct GpuSkinnedInstanceData
{
    uint meshHandle;
    uint skinVertexBase;
    uint boneMatrixOffset;
    uint previousBoneMatrixOffset;
    uint boneCount;
    uint pad0;
    uint pad1;
    uint pad2;
};

static const uint WOLF_INFLUENCES_PER_VERTEX = 4u;

/// Blends a vertex through the specified bone-matrix range.
float3 WolfSkinPosition(
    float3 bindPosition,
    uint4 boneIndices,
    float4 boneWeights,
    StructuredBuffer<GpuBoneMatrix> boneMatrices,
    uint matrixOffset,
    uint boneCount)
{
    float3 skinned = float3(0.0f, 0.0f, 0.0f);

    [unroll]
    for (uint influence = 0u; influence < WOLF_INFLUENCES_PER_VERTEX; ++influence)
    {
        float weight = boneWeights[influence];
        if (weight <= 0.0f)
        {
            continue;
        }

        uint boneIndex = min(boneIndices[influence], boneCount - 1u);
        float4x4 skin = boneMatrices[matrixOffset + boneIndex].skin;

        // Row-vector convention throughout the engine: mul(vector, matrix).
        skinned += mul(float4(bindPosition, 1.0f), skin).xyz * weight;
    }

    return skinned;
}
