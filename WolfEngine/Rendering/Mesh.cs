#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Silk.NET.Assimp;

namespace WolfEngine;

public class Mesh
{
    public Mesh(string filename)
    {
        var assimp = Assimp.GetApi();
        if (string.IsNullOrWhiteSpace(filename))
        {
            throw new ArgumentException("Filename cannot be null or empty.", nameof(filename));
        }

        var fullPath = Path.IsPathRooted(filename)
            ? filename
            : Path.Combine(AppContext.BaseDirectory, filename);

        if (!System.IO.File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Mesh file '{fullPath}' was not found.", fullPath);
        }

        const PostProcessSteps postProcess = PostProcessSteps.Triangulate
                                            | PostProcessSteps.JoinIdenticalVertices
                                            | PostProcessSteps.PreTransformVertices;

        unsafe
        {
            Scene* scene = assimp.ImportFile(fullPath, (uint)postProcess);
            if (scene == null)
            {
                throw new InvalidOperationException($"Failed to load mesh from '{fullPath}'.");
            }

            try
            {
                if (scene->MNumMeshes == 0)
                {
                    throw new InvalidOperationException($"No meshes were found in '{fullPath}'.");
                }

                var mesh = scene->MMeshes[0];
                if (mesh->MVertices == null)
                {
                    throw new InvalidOperationException($"Mesh '{fullPath}' does not contain position data.");
                }

                var vertexCount = mesh->MNumVertices;
                var vertices = new Vector4[vertexCount];
                var normals = new Vector3[vertexCount];
                var rawVertices = mesh->MVertices;
                var rawNormals = mesh->MNormals;
                for (var i = 0; i < vertexCount; i++)
                {
                    var position = rawVertices[i];
                    vertices[i] = new Vector4(position.X, position.Y, position.Z, 1.0f);
                    if (rawNormals is not null)
                    {
                        var normal = rawNormals[i];
                        normals[i] = Vector3.Normalize(new Vector3(normal.X, normal.Y, normal.Z));
                    }
                }

                var indexList = new List<uint>((int)(mesh->MNumFaces * 3));
                var faces = mesh->MFaces;
                for (var faceIndex = 0; faceIndex < mesh->MNumFaces; faceIndex++)
                {
                    var face = faces[faceIndex];
                    for (var i = 0; i < face.MNumIndices; i++)
                    {
                        indexList.Add(face.MIndices[i]);
                    }
                }

                if (indexList.Count == 0)
                {
                    throw new InvalidOperationException($"Mesh '{fullPath}' does not contain index data.");
                }

                Vertices = vertices;
                Indices = indexList.ToArray();
                Normals = rawNormals is not null
                    ? normals
                    : GenerateVertexNormals(vertices, Indices);
            }
            finally
            {
                assimp.ReleaseImport(scene);
            }
        }
    }
    
    public Mesh(IReadOnlyList<Vector4> vertices, IReadOnlyList<uint> indices, IReadOnlyList<Vector3>? normals = null)
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
    }

    public Vector4[] Vertices { get; }
    public uint[] Indices { get; }
    public Vector3[] Normals { get; }

    private static Vector3[] GenerateVertexNormals(IReadOnlyList<Vector4> vertices, IReadOnlyList<uint> indices)
    {
        var normals = new Vector3[vertices.Count];

        for (var i = 0; i < indices.Count; i += 3)
        {
            if (i + 2 >= indices.Count)
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
