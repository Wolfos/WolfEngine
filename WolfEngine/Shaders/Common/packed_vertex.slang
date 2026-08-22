// Backend-stable view of the engine's 48-byte packed mesh vertex.
//
// Metal aligns a float3 struct member to 16 bytes while Direct3D packs it to 12. Grouping the
// existing data into three float4 values preserves the CPU buffer's exact byte layout on both
// backends:
//   0..11 position, 12..23 normal, 24..31 uv, 32..47 tangent.
struct PackedMeshVertex
{
    float4 positionNormalX;
    float4 normalYZUv;
    float4 tangent;
};

float3 GetPackedVertexPosition(PackedMeshVertex vertex)
{
    return vertex.positionNormalX.xyz;
}

float3 GetPackedVertexNormal(PackedMeshVertex vertex)
{
    return float3(vertex.positionNormalX.w, vertex.normalYZUv.xy);
}

float2 GetPackedVertexUv(PackedMeshVertex vertex)
{
    return vertex.normalYZUv.zw;
}
