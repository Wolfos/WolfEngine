// Alpha testing for ray hits on alpha-tested (non-opaque) TLAS instances, shared by every ray-traced pass.
// It needs only the instance, material, mesh, vertex and index tables, which each of those passes binds.

import "common_bindless.slang";
import "draw_data.slang";
import "packed_vertex.slang";
import "raytracing_common.slang";

static const uint RT_MAX_INSTANCE_COUNT = 1048576u;
static const uint RT_MAX_MATERIAL_COUNT = 1048576u;

uint RtResolveMaterialIndex(uint materialHandle)
{
    if (!IsPackedHandleValid(materialHandle))
    {
        return 0u;
    }

    uint materialIndex = GetHandleIndex(materialHandle);
    return materialIndex > 0u && materialIndex < RT_MAX_MATERIAL_COUNT ? materialIndex : 0u;
}

/// The material alpha test that rasterisation applies in gbuffer.slang and shadow_map.slang, applied to a ray hit:
/// the albedo texture's alpha times the base color's alpha must reach the material's cutoff.
struct RtAlphaTest : IWolfRayAlphaTest
{
    StructuredBuffer<GpuInstanceData> instances;
    StructuredBuffer<GpuMaterialData> materials;
    StructuredBuffer<uint> instanceIndexToInstanceHandle;
    StructuredBuffer<GpuMeshData> meshes;
    StructuredBuffer<PackedMeshVertex> packedVertices;
    StructuredBuffer<uint> packedIndices;

    bool AcceptHit(uint instanceIndex, uint primitiveIndex, float2 barycentrics)
    {
        // Anything that cannot be resolved counts as a hit, the way an opaque surface would, so a stale table
        // entry never opens a hole in a shadow.
        if (instanceIndex >= RT_MAX_INSTANCE_COUNT)
        {
            return true;
        }

        uint instanceHandle = instanceIndexToInstanceHandle[instanceIndex];
        if (!IsPackedHandleValid(instanceHandle))
        {
            return true;
        }

        uint drawInstanceIndex = GetHandleIndex(instanceHandle);
        if (drawInstanceIndex == 0u || drawInstanceIndex >= RT_MAX_INSTANCE_COUNT)
        {
            return true;
        }

        GpuInstanceData instanceData = instances[drawInstanceIndex];
        GpuMaterialData material = materials[RtResolveMaterialIndex(instanceData.materialHandle)];
        float alphaCutoff = material.metallicRoughness.z;
        if (alphaCutoff <= 0.0f)
        {
            return true;
        }

        if (!IsPackedHandleValid(instanceData.meshHandle))
        {
            return true;
        }

        uint meshIndex = GetHandleIndex(instanceData.meshHandle);
        if (meshIndex == 0u)
        {
            return true;
        }

        GpuMeshData meshData = meshes[meshIndex];
        uint firstIndex = primitiveIndex * 3u;
        if (meshData.indexCount == 0u || firstIndex + 2u >= meshData.indexCount)
        {
            return true;
        }

        uint i0 = packedIndices[meshData.startIndex + firstIndex + 0u] + meshData.baseVertex;
        uint i1 = packedIndices[meshData.startIndex + firstIndex + 1u] + meshData.baseVertex;
        uint i2 = packedIndices[meshData.startIndex + firstIndex + 2u] + meshData.baseVertex;
        float b1 = barycentrics.x;
        float b2 = barycentrics.y;
        float b0 = 1.0f - b1 - b2;
        float2 uv =
            GetPackedVertexUv(packedVertices[i0]) * b0 +
            GetPackedVertexUv(packedVertices[i1]) * b1 +
            GetPackedVertexUv(packedVertices[i2]) * b2;
        float2 materialUv = uv * material.uvOffsetScale.zw + material.uvOffsetScale.xy;

        // Level 0 keeps the coverage the raster passes see up close. Coarser levels average alpha toward the
        // middle and thin foliage out, which a ray cone would need to correct for before it can pick a level.
        Texture2D<float4> albedoTexture = GetTexture(material.albedoHandle);
        float alpha =
            albedoTexture.SampleLevel(GetSampler(material.samplerHandle), materialUv, 0.0f).a *
            material.baseColor.a;
        return alpha >= alphaCutoff;
    }
};
