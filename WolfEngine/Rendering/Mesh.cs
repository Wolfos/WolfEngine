#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.AssetPipeline;

namespace WolfEngine;

[RuntimeAsset(AssetType.Mesh, typeof(ImportedMeshAssetFile), typeof(IMeshRuntimeAssetResolver))]
public class Mesh
{
    public Vector4[] Vertices { get; }
    public uint[] Indices { get; }
    public Vector3[] Normals { get; }
    public Vector4[] Tangents { get; }
    public Vector2[] UVs { get; }
    public BoundingSphere BoundingSphere { get; }
    public Box BoundingBox { get; }

    /// <summary>
    /// Four bone influences per vertex, flattened, or null for static geometry. Kept out of the
    /// packed vertex format on purpose: widening every vertex in the world by 32 bytes to serve
    /// the handful of skinned meshes would be a poor trade.
    /// </summary>
    public uint[]? BoneIndices { get; }
    public float[]? BoneWeights { get; }

    /// <summary>
    /// For a per-instance skinned clone, the bind-pose mesh the skinning pass reads from.
    /// Null for ordinary meshes, including the skinned source mesh itself.
    /// </summary>
    internal Mesh? SkinningSource { get; }

    // GPU resources are set by the renderer after creation, and cleared again when it releases them
    internal IGfxBuffer? VertexBuffer { get; set; }
    internal IGfxBuffer? IndexBuffer { get; set; }
    internal uint StrideInBytes { get; set; }
    internal uint IndexCount { get; set; }
    internal ulong PackedVertexOffsetBytes { get; set; }
    internal ulong PackedIndexOffsetBytes { get; set; }
    internal int PackedBaseVertex { get; set; }

    /// <summary>Offset of this mesh's influences in the renderer's skin attribute buffer.</summary>
    internal ulong PackedSkinOffsetBytes { get; set; }

    internal bool HasSkinAttributeAllocation { get; set; }

    [MemberNotNullWhen(true, nameof(BoneIndices), nameof(BoneWeights))]
    public bool IsSkinned => BoneIndices is not null && BoneWeights is not null;

    /// <summary>True when this mesh owns a per-instance vertex range written by the skinning pass.</summary>
    internal bool IsSkinnedInstance => SkinningSource is not null;

    /// <summary>Whether the renderer has backed this mesh with GPU geometry. Exposed for diagnostics.</summary>
    public bool HasGpuVertexRange => VertexBuffer is not null;

    public Mesh(
        IReadOnlyList<Vector4> vertices,
        IReadOnlyList<uint> indices,
        IReadOnlyList<Vector3>? normals = null,
        IReadOnlyList<Vector2>? uvs = null,
        IReadOnlyList<Vector4>? tangents = null,
        IReadOnlyList<uint>? boneIndices = null,
        IReadOnlyList<float>? boneWeights = null)
    {
        Vertices = vertices?.ToArray() ?? throw new ArgumentNullException(nameof(vertices));
        if (Vertices.Length == 0)
        {
            throw new ArgumentException("Mesh must contain at least one vertex.", nameof(vertices));
        }

        Indices = indices?.ToArray() ?? throw new ArgumentNullException(nameof(indices));
        if (Indices.Length == 0)
        {
            throw new ArgumentException("Mesh must contain at least one index.", nameof(indices));
        }

        if (tangents is not null)
        {
            if (tangents.Count != Vertices.Length)
            {
                throw new ArgumentException("Tangent count must match vertex count.", nameof(tangents));
            }

            Tangents = tangents.ToArray();
        }
        else
        {
            Tangents = Enumerable.Repeat(new Vector4(1, 0, 0, 1), Vertices.Length).ToArray();
        }

        if (normals is not null)
        {
            if (normals.Count != Vertices.Length)
            {
                throw new ArgumentException("Normal count must match vertex count.", nameof(normals));
            }

            Normals = normals.ToArray();
        }
        else
        {
            Normals = GenerateVertexNormals(Vertices, Indices);
        }

        if (uvs is not null)
        {
            if (uvs.Count != Vertices.Length)
            {
                throw new ArgumentException("UV count must match vertex count.", nameof(uvs));
            }

            UVs = uvs.ToArray();
        }
        else
        {
            UVs = Enumerable.Repeat(Vector2.Zero, Vertices.Length).ToArray();
        }

        if (boneIndices is not null && boneWeights is not null)
        {
            var expected = Vertices.Length * InfluencesPerVertex;
            if (boneIndices.Count != expected || boneWeights.Count != expected)
            {
                throw new ArgumentException(
                    $"Skin influence arrays must hold {expected} entries ({InfluencesPerVertex} per vertex), " +
                    $"but hold {boneIndices.Count} indices and {boneWeights.Count} weights.",
                    nameof(boneIndices));
            }

            BoneIndices = boneIndices.ToArray();
            BoneWeights = boneWeights.ToArray();
        }
        else if (boneIndices is not null || boneWeights is not null)
        {
            throw new ArgumentException("Bone indices and bone weights must be supplied together.", nameof(boneIndices));
        }

        BoundingSphere = ComputeBoundingSphere(Vertices);
        BoundingBox = ComputeBoundingBox(Vertices);
    }

    /// <summary>
    /// Creates a per-instance skinned clone. The CPU-side geometry arrays are aliased rather than
    /// copied — every instance shares one bind pose, and only the GPU vertex range differs — so
    /// spawning a character does not duplicate its mesh in managed memory.
    /// </summary>
    private Mesh(Mesh source, float boundsExpansion)
    {
        Vertices = source.Vertices;
        Indices = source.Indices;
        Normals = source.Normals;
        Tangents = source.Tangents;
        UVs = source.UVs;
        BoneIndices = source.BoneIndices;
        BoneWeights = source.BoneWeights;
        SkinningSource = source;

        // A deformed pose leaves the bind pose's bounds, and the exact per-frame bounds are not
        // known until after skinning has run on the GPU. Expanding is the conservative choice:
        // it costs some culling efficiency, where being too tight would pop limbs out of frame.
        BoundingSphere = new BoundingSphere(
            source.BoundingSphere.Center,
            source.BoundingSphere.Radius * boundsExpansion);
        BoundingBox = new Box
        {
            Center = source.BoundingBox.Center,
            Size = source.BoundingBox.Size * boundsExpansion
        };
    }

    /// <summary>Number of bone influences stored per vertex. Matches the skinning compute shader.</summary>
    public const int InfluencesPerVertex = 4;

    internal Mesh CreateSkinnedInstance(float boundsExpansion)
    {
        if (IsSkinned == false)
        {
            throw new InvalidOperationException("Only a skinned mesh can produce a skinned instance.");
        }

        if (IsSkinnedInstance)
        {
            throw new InvalidOperationException("A skinned instance cannot itself be instanced.");
        }

        return new Mesh(this, boundsExpansion);
    }


    private static Vector3[] GenerateVertexNormals(Vector4[] vertices, uint[] indices)
    {
        var normals = new Vector3[vertices.Length];

        for (var i = 0; i < indices.Length; i += 3)
        {
            if (i + 2 >= indices.Length)
            {
                break;
            }

            var index0 = (int)indices[i];
            var index1 = (int)indices[i + 1];
            var index2 = (int)indices[i + 2];

            var p0 = ToVector3(vertices[index0]);
            var p1 = ToVector3(vertices[index1]);
            var p2 = ToVector3(vertices[index2]);

            var edge1 = p1 - p0;
            var edge2 = p2 - p0;

            var faceNormal = Vector3.Cross(edge1, edge2);
            if (faceNormal.LengthSquared() <= 0.0f)
            {
                continue;
            }

            normals[index0] += faceNormal;
            normals[index1] += faceNormal;
            normals[index2] += faceNormal;
        }

        for (var i = 0; i < normals.Length; i++)
        {
            var normal = normals[i];
            if (normal.LengthSquared() > 0.0f)
            {
                normals[i] = Vector3.Normalize(normal);
            }
            else
            {
                normals[i] = Vector3.UnitY;
            }
        }

        return normals;
    }

    private static Vector3 ToVector3(Vector4 vector)
    {
        return new Vector3(vector.X, vector.Y, vector.Z);
    }

    private static BoundingSphere ComputeBoundingSphere(Vector4[] vertices)
    {
        var min = ToVector3(vertices[0]);
        var max = min;

        for (var i = 1; i < vertices.Length; i++)
        {
            var point = ToVector3(vertices[i]);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        var center = (min + max) * 0.5f;
        var radiusSquared = 0.0f;
        for (var i = 0; i < vertices.Length; i++)
        {
            var point = ToVector3(vertices[i]);
            var distanceSquared = Vector3.DistanceSquared(center, point);
            if (distanceSquared > radiusSquared)
            {
                radiusSquared = distanceSquared;
            }
        }

        return new BoundingSphere(center, MathF.Sqrt(radiusSquared));
    }

    private static Box ComputeBoundingBox(Vector4[] vertices)
    {
        var min = ToVector3(vertices[0]);
        var max = min;

        for (var i = 1; i < vertices.Length; i++)
        {
            var point = ToVector3(vertices[i]);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        return new Box
        {
            Center = (min + max) * 0.5f,
            Size = max - min
        };
    }
}
