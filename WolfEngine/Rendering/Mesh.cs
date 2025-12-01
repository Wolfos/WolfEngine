#nullable enable
using System.Numerics;
using System.Linq;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine;

public class Mesh
{
    public Vector4[] Vertices { get; }
    public uint[] Indices { get; }
    public Vector3[] Normals { get; }
    public Vector4[] Tangents { get; }
    public Vector2[] UVs { get; }
    
    // GPU resources are set by the renderer after creation
    internal IGfxBuffer VertexBuffer { get; set; }
    internal IGfxBuffer IndexBuffer { get; set; }
    internal uint StrideInBytes { get; set; }
    internal uint IndexCount { get; set; }
    public Mesh(
        IReadOnlyList<Vector4> vertices,
        IReadOnlyList<uint> indices,
        IReadOnlyList<Vector3>? normals = null,
        IReadOnlyList<Vector2>? uvs = null,
        IReadOnlyList<Vector4>? tangents = null)
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
}
