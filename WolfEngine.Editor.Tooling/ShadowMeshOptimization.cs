using System.Numerics;
using Evergine.Bindings.MeshOptimizer;
using MeshStream = Evergine.Bindings.MeshOptimizer.Stream;

namespace WolfEngine.Importing;

/// <summary>Builds lossless shadow streams at import, after main geometry optimization.</summary>
public static class ShadowMeshOptimization
{
    public static Mesh Optimize(Mesh mesh)
    {
        var opaque = Generate(mesh, false);
        var alpha = ReferenceEquals(opaque, mesh.Indices) ? mesh.Indices : Generate(mesh, true);
        return new Mesh(mesh.Vertices, mesh.Indices, mesh.Normals, mesh.UVs, mesh.Tangents,
            mesh.BoneIndices, mesh.BoneWeights, opaque, alpha);
    }

    private static unsafe uint[] Generate(Mesh mesh, bool includeUv)
    {
        if (mesh.Indices.Length % 3 != 0) return mesh.Indices;
        // The native optimizer assumes all indices address valid vertices.
        foreach (var index in mesh.Indices)
            if (index >= mesh.Vertices.Length) throw new ArgumentException("Mesh contains an out-of-range index.", nameof(mesh));

        var shadow = new uint[mesh.Indices.Length];
        fixed (Vector4* positions = mesh.Vertices)
        fixed (Vector2* uvs = mesh.UVs)
        fixed (uint* boneIndices = mesh.BoneIndices)
        fixed (float* boneWeights = mesh.BoneWeights)
        fixed (uint* indices = mesh.Indices)
        fixed (uint* destination = shadow)
        {
            // Compare XYZ only, never normals, tangents or the position's unused W.
            MeshStream* streams = stackalloc MeshStream[4];
            var count = 0;
            streams[count++] = new MeshStream { Data = positions, Size = 12, Stride = 16 };
            if (includeUv) streams[count++] = new MeshStream { Data = uvs, Size = 8, Stride = 8 };
            if (mesh.IsSkinned)
            {
                // Exact influence equality guarantees equal current and future deformation.
                streams[count++] = new MeshStream { Data = boneIndices, Size = 16, Stride = 16 };
                streams[count++] = new MeshStream { Data = boneWeights, Size = 16, Stride = 16 };
            }
            MeshOptimizer.GenerateShadowIndexBufferMulti(destination, indices, (nuint)shadow.Length,
                (nuint)mesh.Vertices.Length, streams, (nuint)count);
            // Avoid extra GPU storage and triangle reordering when there is no seam to weld.
            if (shadow.AsSpan().SequenceEqual(mesh.Indices)) return mesh.Indices;
            MeshOptimizer.OptimizeVertexCache(destination, destination, (nuint)shadow.Length, (nuint)mesh.Vertices.Length);
        }
        return shadow;
    }
}
