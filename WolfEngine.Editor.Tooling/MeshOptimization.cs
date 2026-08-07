using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using Evergine.Bindings.MeshOptimizer;
using MeshStream = Evergine.Bindings.MeshOptimizer.Stream;

namespace WolfEngine.Importing;

/// <summary>
/// One mesh's vertex streams and triangle indices as they enter and leave <see cref="MeshOptimization"/>.
/// Optional streams are null when the source file did not provide them, and stay null on the way out so
/// the importer keeps deciding what to synthesise.
/// </summary>
/// <param name="Positions">One <see cref="Vector4"/> per vertex; only XYZ is treated as the position.</param>
/// <param name="BoneIndices">Flattened, <see cref="Mesh.InfluencesPerVertex"/> entries per vertex.</param>
/// <param name="BoneWeights">Flattened, <see cref="Mesh.InfluencesPerVertex"/> entries per vertex.</param>
public sealed record MeshGeometry(
    Vector4[] Positions,
    Vector3[]? Normals,
    Vector2[]? Uvs,
    Vector4[]? Tangents,
    uint[]? BoneIndices,
    float[]? BoneWeights,
    uint[] Indices);

/// <summary>
/// Runs meshoptimizer over a freshly parsed mesh so the geometry that reaches the asset library is
/// already laid out for the GPU. The steps have to run in this order — each one assumes the previous
/// one has happened:
/// <list type="number">
/// <item>Indexing: weld vertices that carry identical data across every stream.</item>
/// <item>Vertex cache optimization: reorder triangles so the post-transform cache is reused.</item>
/// <item>Vertex fetch optimization: reorder vertices so a draw walks the vertex buffer forwards.</item>
/// <item>Index filtering: drop triangles whose corners collapsed onto the same position.</item>
/// </list>
/// Overdraw optimization and shadow indexing are deliberately left out: the first trades vertex cache
/// efficiency for a depth-sorted order that only pays off under heavy overdraw, and the second needs a
/// second index buffer that the renderer has no depth-only path for. Vertex quantization is left out
/// because the runtime vertex format is float32 end to end, so quantizing here would cost precision
/// without saving a byte.
/// </summary>
public static class MeshOptimization
{
    /// <summary>
    /// Bytes of each position compared when detecting degenerate triangles. Positions are stored as a
    /// <see cref="Vector4"/>, and the padding W would otherwise take part in the comparison.
    /// </summary>
    private const int PositionSizeBytes = 3 * sizeof(float);

    public static MeshGeometry Optimize(MeshGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        var vertexCount = geometry.Positions.Length;
        var indexCount = geometry.Indices.Length;

        // Everything below assumes a triangle list over a non-empty vertex buffer. A mesh that does not
        // qualify is passed through untouched rather than rejected; validation belongs to the caller.
        if (vertexCount == 0 || indexCount == 0 || indexCount % 3 != 0)
        {
            return geometry;
        }

        // 1. Indexing.
        var weldRemap = new uint[vertexCount];
        var weldedVertexCount = GenerateVertexRemap(geometry, weldRemap);
        var optimized = ApplyVertexRemap(geometry, weldRemap, weldedVertexCount);

        // 2. Vertex cache optimization. Reorders triangles only, so vertex data is left alone.
        OptimizeVertexCacheInPlace(optimized.Indices, weldedVertexCount);

        // 3. Vertex fetch optimization.
        optimized = OptimizeVertexFetch(optimized, weldedVertexCount);

        // 4. Index filtering.
        var filteredIndices = FilterDegenerateTriangles(optimized);
        if (filteredIndices is null)
        {
            return optimized;
        }

        // Dropping triangles can leave vertices that nothing references any more, which is exactly what
        // the fetch step just removed. Running it again is cheap and keeps the promise that the vertex
        // buffer holds only vertices the draw actually reads.
        return OptimizeVertexFetch(optimized with { Indices = filteredIndices }, optimized.Positions.Length);
    }

    /// <summary>
    /// Welds vertices whose data is byte-identical across every stream. Comparing all streams rather
    /// than positions alone is what keeps a hard edge hard: two corners sharing a position but carrying
    /// different normals, UVs or skin influences stay separate vertices.
    /// </summary>
    private static unsafe int GenerateVertexRemap(MeshGeometry geometry, uint[] remap)
    {
        var vertexCount = geometry.Positions.Length;
        var handles = new List<MemoryHandle>(6);
        var streams = new List<MeshStream>(6);

        try
        {
            AddStream(geometry.Positions, 1);
            AddStream(geometry.Normals, 1);
            AddStream(geometry.Uvs, 1);
            AddStream(geometry.Tangents, 1);
            AddStream(geometry.BoneIndices, Mesh.InfluencesPerVertex);
            AddStream(geometry.BoneWeights, Mesh.InfluencesPerVertex);

            var streamArray = streams.ToArray();
            fixed (uint* indices = geometry.Indices)
            fixed (uint* remapPointer = remap)
            fixed (MeshStream* streamPointer = streamArray)
            {
                return (int)MeshOptimizer.GenerateVertexRemapMulti(
                    remapPointer,
                    indices,
                    (nuint)geometry.Indices.Length,
                    (nuint)vertexCount,
                    streamPointer,
                    (nuint)streamArray.Length);
            }
        }
        finally
        {
            foreach (var handle in handles)
            {
                handle.Dispose();
            }
        }

        void AddStream<T>(T[]? data, int componentsPerVertex) where T : unmanaged
        {
            if (data is null)
            {
                return;
            }

            var elementSize = (nuint)(Unsafe.SizeOf<T>() * componentsPerVertex);
            var handle = data.AsMemory().Pin();
            handles.Add(handle);
            streams.Add(new MeshStream { Data = handle.Pointer, Size = elementSize, Stride = elementSize });
        }
    }

    private static unsafe void OptimizeVertexCacheInPlace(uint[] indices, int vertexCount)
    {
        fixed (uint* indexPointer = indices)
        {
            MeshOptimizer.OptimizeVertexCache(indexPointer, indexPointer, (nuint)indices.Length, (nuint)vertexCount);
        }
    }

    /// <summary>
    /// Reorders vertices into the order the (already cache-optimized) index buffer first touches them,
    /// and drops any vertex no triangle references.
    /// </summary>
    private static unsafe MeshGeometry OptimizeVertexFetch(MeshGeometry geometry, int vertexCount)
    {
        var remap = new uint[vertexCount];
        int fetchVertexCount;
        fixed (uint* remapPointer = remap)
        fixed (uint* indices = geometry.Indices)
        {
            fetchVertexCount = (int)MeshOptimizer.OptimizeVertexFetchRemap(
                remapPointer,
                indices,
                (nuint)geometry.Indices.Length,
                (nuint)vertexCount);
        }

        return ApplyVertexRemap(geometry, remap, fetchVertexCount);
    }

    /// <summary>
    /// Removes triangles with two or more corners at the same position. Assimp's triangulation of
    /// concave or slivered polygons emits them, and they cost a full triangle setup to cover no pixels.
    /// Returns null when there is nothing to remove, or when every triangle is degenerate — a mesh that
    /// draws nothing is still better imported than failed.
    /// </summary>
    private static unsafe uint[]? FilterDegenerateTriangles(MeshGeometry geometry)
    {
        var indexCount = geometry.Indices.Length;
        var filtered = new uint[indexCount];
        int filteredIndexCount;
        fixed (uint* destination = filtered)
        fixed (uint* indices = geometry.Indices)
        fixed (Vector4* positions = geometry.Positions)
        {
            filteredIndexCount = (int)MeshOptimizer.FilterIndexBuffer(
                destination,
                indices,
                (nuint)indexCount,
                positions,
                (nuint)geometry.Positions.Length,
                PositionSizeBytes,
                (nuint)sizeof(Vector4));
        }

        if (filteredIndexCount == indexCount || filteredIndexCount == 0)
        {
            return null;
        }

        Array.Resize(ref filtered, filteredIndexCount);
        return filtered;
    }

    private static unsafe MeshGeometry ApplyVertexRemap(MeshGeometry geometry, uint[] remap, int destinationVertexCount)
    {
        var sourceVertexCount = geometry.Positions.Length;
        var indices = new uint[geometry.Indices.Length];
        fixed (uint* destination = indices)
        fixed (uint* source = geometry.Indices)
        fixed (uint* remapPointer = remap)
        {
            MeshOptimizer.RemapIndexBuffer(destination, source, (nuint)geometry.Indices.Length, remapPointer);
        }

        return new MeshGeometry(
            RemapStream(geometry.Positions, sourceVertexCount, destinationVertexCount, 1, remap)!,
            RemapStream(geometry.Normals, sourceVertexCount, destinationVertexCount, 1, remap),
            RemapStream(geometry.Uvs, sourceVertexCount, destinationVertexCount, 1, remap),
            RemapStream(geometry.Tangents, sourceVertexCount, destinationVertexCount, 1, remap),
            RemapStream(geometry.BoneIndices, sourceVertexCount, destinationVertexCount, Mesh.InfluencesPerVertex, remap),
            RemapStream(geometry.BoneWeights, sourceVertexCount, destinationVertexCount, Mesh.InfluencesPerVertex, remap),
            indices);
    }

    private static unsafe T[]? RemapStream<T>(
        T[]? data,
        int sourceVertexCount,
        int destinationVertexCount,
        int componentsPerVertex,
        uint[] remap) where T : unmanaged
    {
        if (data is null)
        {
            return null;
        }

        var destination = new T[destinationVertexCount * componentsPerVertex];
        fixed (T* destinationPointer = destination)
        fixed (T* sourcePointer = data)
        fixed (uint* remapPointer = remap)
        {
            MeshOptimizer.RemapVertexBuffer(
                destinationPointer,
                sourcePointer,
                (nuint)sourceVertexCount,
                (nuint)(Unsafe.SizeOf<T>() * componentsPerVertex),
                remapPointer);
        }

        return destination;
    }
}
