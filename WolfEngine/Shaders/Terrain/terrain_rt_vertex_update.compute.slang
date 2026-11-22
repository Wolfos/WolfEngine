import "../Common/common_bindless.slang";
import "terrain_height_sampling.slang";

RWByteAddressBuffer g_TerrainRtVertices : register(u0);

cbuffer TerrainRtVertexUpdateParams : register(b11)
{
    uint heightmapHandle;
    uint resolutionInQuads;
    uint heightmapWidth;
    uint heightmapHeight;
    float heightScale;
    float3 _pad0;
    float4 chunkOriginSize;
    float4 heightmapUvScaleOffset;
};

[numthreads(64, 1, 1)]
void TerrainRayTracingVertexUpdateCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint vertsPerAxis = resolutionInQuads + 1u;
    uint vertexCount = vertsPerAxis * vertsPerAxis;
    uint vertexIndex = dispatchThreadId.x;
    if (vertexIndex >= vertexCount)
    {
        return;
    }

    uint x = vertexIndex % vertsPerAxis;
    uint z = vertexIndex / vertsPerAxis;
    float2 baseUv = resolutionInQuads > 0u
        ? float2(float(x), float(z)) / float(resolutionInQuads)
        : float2(0.0f, 0.0f);
    float2 terrainUv = heightmapUvScaleOffset.zw + baseUv * heightmapUvScaleOffset.xy;
    float height = SampleTerrainHeightBicubic(
        GetTexture(heightmapHandle),
        heightmapWidth,
        heightmapHeight,
        terrainUv,
        heightScale);

    float localX = chunkOriginSize.x + baseUv.x * chunkOriginSize.z;
    float localZ = chunkOriginSize.y + baseUv.y * chunkOriginSize.w;
    uint byteOffset = vertexIndex * 12u;
    g_TerrainRtVertices.Store(byteOffset + 0u, asuint(localX));
    g_TerrainRtVertices.Store(byteOffset + 4u, asuint(height));
    g_TerrainRtVertices.Store(byteOffset + 8u, asuint(localZ));
}
