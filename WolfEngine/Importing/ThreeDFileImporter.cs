using System.Numerics;
using Silk.NET.Assimp;
using WolfEngine.ECS;
using File = System.IO.File;

namespace WolfEngine.Importing;

public class ThreeDFileImporter : IThreeDFileImporter
{
    public unsafe ImportedScene Import(string filename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        
        var assimp = Assimp.GetApi();
        
        var fullPath = Path.IsPathRooted(filename)
            ? filename
            : Path.Combine(AppContext.BaseDirectory, filename);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Mesh file '{fullPath}' was not found.", fullPath);
        }

        const PostProcessSteps postProcess = PostProcessSteps.Triangulate
                                             | PostProcessSteps.JoinIdenticalVertices
                                             | PostProcessSteps.MakeLeftHanded
                                             | PostProcessSteps.FlipWindingOrder
                                             | PostProcessSteps.FlipUVs;
        
        var scene = assimp.ImportFile(fullPath, (uint)postProcess);
        if (scene == null)
        {
            throw new InvalidOperationException($"Failed to load mesh from '{fullPath}'.");
        }

        var meshes = new List<ImportedMesh>();
        var materials = new List<ImportedMaterial>();
        var textures = new List<ImportedTexture>();
        var meshData = new List<(Mesh mesh, int materialIndex)>();

        try
        {
            // Materials
            for (var materialIndex = 0; materialIndex < scene->MNumMaterials; materialIndex++)
            {
                var material = scene->MMaterials[materialIndex];

                var baseColor = Vector4.One;
                assimp.GetMaterialColor(material, Assimp.MaterialColorDiffuseBase, 0, 0, ref baseColor);
                
                materials.Add(new (baseColor));
            }
            
            // Textures
            for (var textureIndex = 0; textureIndex < scene->MNumTextures; textureIndex++)
            {
                var texture = scene->MTextures[textureIndex];
                var importedTexture = new ImportedTexture(texture->MFilename.AsString);
                textures.Add(importedTexture);
            }
            
            // Mesh data (geometry + material index)
            for (var meshIndex = 0; meshIndex < scene->MNumMeshes; meshIndex++)
            {
                var mesh = scene->MMeshes[meshIndex];
                
                var vertexCount = mesh->MNumVertices;
                var vertices = new Vector4[vertexCount];
                var normals = new Vector3[vertexCount];
                var rawVertices = mesh->MVertices;
                var rawNormals = mesh->MNormals;
                for (var i = 0; i < vertexCount; i++)
                {
                    var position = rawVertices[i];
                    vertices[i] = new(position.X, position.Y, position.Z, 1.0f);
                    if (rawNormals is not null)
                    {
                        var normal = rawNormals[i];
                        normals[i] = Vector3.Normalize(new(normal.X, normal.Y, normal.Z));
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

                var materialIndex = (int)mesh->MMaterialIndex;
                var importedMesh = new Mesh(vertices, indexList, rawNormals is not null ? normals : null);
                meshData.Add((importedMesh, materialIndex));
            }

            // Traverse node graph to create placed meshes with transforms
            TraverseNode(scene->MRootNode, scene, meshData, meshes);
        }
        finally
        {
            assimp.ReleaseImport(scene);
        }

        return new ImportedScene(materials, textures, meshes);
    }

    private static unsafe void TraverseNode(
        Node* node,
        Scene* scene,
        IReadOnlyList<(Mesh mesh, int materialIndex)> meshData,
        List<ImportedMesh> output)
    {
        if (node is null)
        {
            return;
        }

        var localTransform = node->MTransformation;

        for (var i = 0; i < node->MNumMeshes; i++)
        {
            var meshIndex = (int)node->MMeshes[i];
            if (meshIndex < 0 || meshIndex >= meshData.Count)
            {
                continue;
            }

            var (mesh, materialIndex) = meshData[meshIndex];
            output.Add(new (node->MName.AsString, GetTransform(localTransform), mesh, materialIndex));
        }

        for (var childIndex = 0; childIndex < node->MNumChildren; childIndex++)
        {
            TraverseNode(node->MChildren[childIndex], scene, meshData, output);
        }
    }

    private static Transform GetTransform(Matrix4x4 m)
    {
        // Convert Assimp matrix to column major
        var packed = new Matrix4x4(
            m.M11, m.M21, m.M31, m.M41,
            m.M12, m.M22, m.M32, m.M42,
            m.M13, m.M23, m.M33, m.M43,
            m.M14, m.M24, m.M34, m.M44);

        return new (packed);
    }
}
