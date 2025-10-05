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
                var rawVertices = mesh->MVertices;
                for (var i = 0; i < vertexCount; i++)
                {
                    var position = rawVertices[i];
                    vertices[i] = new Vector4(position.X, position.Y, position.Z, 1.0f);
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
            }
            finally
            {
                assimp.ReleaseImport(scene);
            }
        }
    }
    
    public Mesh(IReadOnlyList<Vector4> vertices, IReadOnlyList<uint> indices)
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
    }

    public Vector4[] Vertices { get; }
    public uint[] Indices { get; }
}
