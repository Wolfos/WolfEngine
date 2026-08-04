import "../Common/common_bindless.slang";

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

float SampleTerrainHeight(float2 uv)
{
    Texture2D<float4> heightmap = GetTexture(heightmapHandle);
    uint safeWidth = max(heightmapWidth, 1u);
    uint safeHeight = max(heightmapHeight, 1u);
    float2 pixel = saturate(uv) * float2(float(safeWidth - 1u), float(safeHeight - 1u));
    uint2 p0 = uint2(floor(pixel));
    uint2 p1 = min(p0 + uint2(1u, 1u), uint2(safeWidth - 1u, safeHeight - 1u));
    float2 t = frac(pixel);

    float h00 = heightmap.Load(int3(int2(p0.x, p0.y), 0)).r;
    float h10 = heightmap.Load(int3(int2(p1.x, p0.y), 0)).r;
    float h01 = heightmap.Load(int3(int2(p0.x, p1.y), 0)).r;
    float h11 = heightmap.Load(int3(int2(p1.x, p1.y), 0)).r;
    return lerp(lerp(h00, h10, t.x), lerp(h01, h11, t.x), t.y) * heightScale;
}

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
    float height = SampleTerrainHeight(terrainUv);

    float localX = chunkOriginSize.x + baseUv.x * chunkOriginSize.z;
    float localZ = chunkOriginSize.y + baseUv.y * chunkOriginSize.w;
    uint byteOffset = vertexIndex * 12u;
    g_TerrainRtVertices.Store(byteOffset + 0u, asuint(localX));
    g_TerrainRtVertices.Store(byteOffset + 4u, asuint(height));
    g_TerrainRtVertices.Store(byteOffset + 8u, asuint(localZ));
}
