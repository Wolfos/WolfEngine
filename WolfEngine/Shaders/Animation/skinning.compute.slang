// Linear-blend skinning into a per-instance range of the packed vertex buffer.
//
// The deformed vertices are written as real geometry rather than being computed in the vertex
// shader, so a skinned character is visible to the ray-traced passes (reflections, DDGI) and reuses
// the existing culling and indirect-draw path without any shader variant.
//
// The G-buffer reuses the position blend with previous-frame bones for motion vectors.

import "skinning_common.slang";

// The source range and the destination range are disjoint parts of the same packed buffer, so one
// read-write binding covers both and the buffer is never bound twice in the same dispatch.
RWByteAddressBuffer g_PackedVertices : register(u0);

// Shared with the G-buffer vertex shader.
StructuredBuffer<GpuSkinVertex> g_SkinVertices : register(t2);
StructuredBuffer<GpuBoneMatrix> g_BoneMatrices : register(t3);

cbuffer SkinningParams : register(b11)
{
    uint vertexCount;
    uint sourceVertexIndex;
    uint destVertexIndex;
    uint skinVertexIndex;
    uint boneMatrixOffset;
    uint boneCount;
    uint2 _pad0;
};

// Must match WolfEngine's packed VertexData: float3 position, float3 normal, float2 uv, float4 tangent.
static const uint WOLF_VERTEX_STRIDE = 48u;
static const uint WOLF_VERTEX_POSITION_OFFSET = 0u;
static const uint WOLF_VERTEX_NORMAL_OFFSET = 12u;
static const uint WOLF_VERTEX_UV_OFFSET = 24u;
static const uint WOLF_VERTEX_TANGENT_OFFSET = 32u;

float3 NormalizeOrFallback(float3 value, float3 fallback)
{
    float lengthSquared = dot(value, value);
    return lengthSquared > 1e-12f ? value * rsqrt(lengthSquared) : fallback;
}

[numthreads(64, 1, 1)]
void SkinningCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint vertex = dispatchThreadId.x;
    if (vertex >= vertexCount || boneCount == 0u)
    {
        return;
    }

    uint sourceOffset = (sourceVertexIndex + vertex) * WOLF_VERTEX_STRIDE;
    uint destOffset = (destVertexIndex + vertex) * WOLF_VERTEX_STRIDE;

    float3 position = asfloat(g_PackedVertices.Load3(sourceOffset + WOLF_VERTEX_POSITION_OFFSET));
    float3 normal = asfloat(g_PackedVertices.Load3(sourceOffset + WOLF_VERTEX_NORMAL_OFFSET));
    uint2 packedUv = g_PackedVertices.Load2(sourceOffset + WOLF_VERTEX_UV_OFFSET);
    float4 tangent = asfloat(g_PackedVertices.Load4(sourceOffset + WOLF_VERTEX_TANGENT_OFFSET));

    GpuSkinVertex skinVertex = g_SkinVertices[skinVertexIndex + vertex];
    uint4 boneIndices = skinVertex.boneIndices;
    float4 boneWeights = skinVertex.boneWeights;

    float3 skinnedPosition = WolfSkinPosition(
        position,
        boneIndices,
        boneWeights,
        g_BoneMatrices,
        boneMatrixOffset,
        boneCount);

    float3 skinnedNormal = float3(0.0f, 0.0f, 0.0f);
    float3 skinnedTangent = float3(0.0f, 0.0f, 0.0f);

    [unroll]
    for (uint influence = 0u; influence < WOLF_INFLUENCES_PER_VERTEX; ++influence)
    {
        float weight = boneWeights[influence];
        if (weight <= 0.0f)
        {
            continue;
        }

        uint boneIndex = min(boneIndices[influence], boneCount - 1u);
        float4x4 skin = g_BoneMatrices[boneMatrixOffset + boneIndex].skin;

        // Rotating normals by the skinning matrix rather than its inverse transpose is exact for
        // rigid and uniformly scaled bones, and wrong only under non-uniform bone scale.
        skinnedNormal += mul(float4(normal, 0.0f), skin).xyz * weight;
        skinnedTangent += mul(float4(tangent.xyz, 0.0f), skin).xyz * weight;
    }

    g_PackedVertices.Store3(destOffset + WOLF_VERTEX_POSITION_OFFSET, asuint(skinnedPosition));
    g_PackedVertices.Store3(destOffset + WOLF_VERTEX_NORMAL_OFFSET, asuint(NormalizeOrFallback(skinnedNormal, normal)));
    g_PackedVertices.Store2(destOffset + WOLF_VERTEX_UV_OFFSET, packedUv);
    g_PackedVertices.Store4(
        destOffset + WOLF_VERTEX_TANGENT_OFFSET,
        asuint(float4(NormalizeOrFallback(skinnedTangent, tangent.xyz), tangent.w)));
}
