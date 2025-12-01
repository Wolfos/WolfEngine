using System.Numerics;
using Silk.NET.Assimp;
using StbImageSharp;
using WolfEngine.ECS;
using File = System.IO.File;
using AssimpMaterial = Silk.NET.Assimp.Material;
using InvalidOperationException = System.InvalidOperationException;

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

        var modelDirectory = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory;

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
        var textureLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Materials (including texture references)
            for (var materialIndex = 0; materialIndex < scene->MNumMaterials; materialIndex++)
            {
                AssimpMaterial* material = scene->MMaterials[materialIndex];

                var baseColor = Vector4.One;
                assimp.GetMaterialColor(material, Assimp.MaterialColorDiffuseBase, 0, 0, ref baseColor);

                var emissiveColor = Vector4.Zero;
                assimp.GetMaterialColor(material, Assimp.MaterialColorEmissive, 0, 0, ref emissiveColor);

                var baseColorTextureIndex = TryLoadMaterialTexture(
                    assimp,
                    material,
                    scene,
                    modelDirectory,
                    textureLookup,
                    textures,
                    TextureSemantic.BaseColor,
                    TextureType.BaseColor,
                    TextureType.Diffuse);

                var normalTextureIndex = TryLoadMaterialTexture(
                    assimp,
                    material,
                    scene,
                    modelDirectory,
                    textureLookup,
                    textures,
                    TextureSemantic.Normal,
                    TextureType.Normals,
                    TextureType.NormalCamera);

                var metallicRoughnessTextureIndex = TryLoadMaterialTexture(
                    assimp,
                    material,
                    scene,
                    modelDirectory,
                    textureLookup,
                    textures,
                    TextureSemantic.MetallicRoughness,
                    TextureType.DiffuseRoughness,
                    TextureType.Metalness);

                var occlusionTextureIndex = TryLoadMaterialTexture(
                    assimp,
                    material,
                    scene,
                    modelDirectory,
                    textureLookup,
                    textures,
                    TextureSemantic.Occlusion,
                    TextureType.AmbientOcclusion,
                    TextureType.Ambient);

                var emissiveTextureIndex = TryLoadMaterialTexture(
                    assimp,
                    material,
                    scene,
                    modelDirectory,
                    textureLookup,
                    textures,
                    TextureSemantic.Emissive,
                    TextureType.Emissive);

                materials.Add(new ImportedMaterial(
                    baseColor,
                    MetallicFactor: 1.0f,
                    RoughnessFactor: 1.0f,
                    EmissiveFactor: new(emissiveColor.X, emissiveColor.Y, emissiveColor.Z),
                    BaseColorTextureIndex: baseColorTextureIndex,
                    NormalTextureIndex: normalTextureIndex,
                    MetallicRoughnessTextureIndex: metallicRoughnessTextureIndex,
                    OcclusionTextureIndex: occlusionTextureIndex,
                    EmissiveTextureIndex: emissiveTextureIndex));
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

    private static bool IsSrgb(TextureSemantic semantic) => semantic is TextureSemantic.BaseColor or TextureSemantic.Emissive;

    private static unsafe int? TryLoadMaterialTexture(
        Assimp assimp,
        AssimpMaterial* material,
        Scene* scene,
        string modelDirectory,
        Dictionary<string, int> textureLookup,
        List<ImportedTexture> textures,
        TextureSemantic semantic,
        params TextureType[] types)
    {
        foreach (var type in types)
        {
            AssimpString path = default;
            var result = assimp.GetMaterialTexture(material, type, 0, &path, null, null, null, null, null, null);
            if (result != Return.Success)
            {
                continue;
            }

            var texturePath = path.AsString;
            if (string.IsNullOrWhiteSpace(texturePath))
            {
                continue;
            }

            var index = GetOrLoadTextureIndex(scene, texturePath, semantic, modelDirectory, textureLookup, textures);
            return index;
        }

        return null;
    }

    private static unsafe int GetOrLoadTextureIndex(
        Scene* scene,
        string texturePath,
        TextureSemantic semantic,
        string modelDirectory,
        Dictionary<string, int> textureLookup,
        List<ImportedTexture> textures)
    {
        var isEmbedded = texturePath.Length > 0 && texturePath[0] == '*';
        var key = isEmbedded ? texturePath : Path.GetFullPath(Path.Combine(modelDirectory, texturePath));

        if (textureLookup.TryGetValue(key, out var existing))
        {
            return existing;
        }

        ImportedTexture importedTexture;
        if (isEmbedded)
        {
            importedTexture = LoadEmbeddedTexture(scene, texturePath, semantic);
        }
        else
        {
            if (!File.Exists(key))
            {
                throw new FileNotFoundException($"Texture file '{key}' was not found.", key);
            }

            importedTexture = LoadExternalTexture(key, semantic);
        }

        var index = textures.Count;
        textures.Add(importedTexture);
        textureLookup[key] = index;
        return index;
    }

    private static unsafe ImportedTexture LoadEmbeddedTexture(Scene* scene, string texturePath, TextureSemantic semantic)
    {
        if (texturePath.Length < 2 || !int.TryParse(texturePath.AsSpan(1), out var embeddedIndex))
        {
            throw new InvalidOperationException($"Invalid embedded texture reference '{texturePath}'.");
        }

        if (embeddedIndex < 0 || embeddedIndex >= scene->MNumTextures)
        {
            throw new InvalidOperationException($"Embedded texture index {embeddedIndex} is out of range.");
        }

        var texture = scene->MTextures[embeddedIndex];
        if (texture == null)
        {
            throw new InvalidOperationException($"Embedded texture {embeddedIndex} was null.");
        }

        return texture->MHeight == 0
            ? LoadCompressedEmbeddedTexture(texture, embeddedIndex, semantic)
            : LoadRawEmbeddedTexture(texture, embeddedIndex, semantic);
    }

    private static unsafe ImportedTexture LoadCompressedEmbeddedTexture(Texture* texture, int embeddedIndex, TextureSemantic semantic)
    {
        var byteLength = checked((int)texture->MWidth);
        var data = new byte[byteLength];
        var raw = (byte*)texture->PcData;
        var source = new ReadOnlySpan<byte>(raw, byteLength);
        source.CopyTo(data);

        var image = ImageResult.FromMemory(data, ColorComponents.RedGreenBlueAlpha);

        return new ImportedTexture(
            $"embedded_{embeddedIndex}",
            image.Width,
            image.Height,
            (int)image.Comp,
            IsSrgb(semantic),
            image.Data);
    }

    private static unsafe ImportedTexture LoadRawEmbeddedTexture(Texture* texture, int embeddedIndex, TextureSemantic semantic)
    {
        var width = checked((int)texture->MWidth);
        var height = checked((int)texture->MHeight);
        var pixelCount = checked(width * height);
        var dest = new byte[pixelCount * 4];

        var source = texture->PcData;
        for (var i = 0; i < pixelCount; i++)
        {
            var texel = source[i];
            var destIndex = i * 4;
            dest[destIndex + 0] = texel.R;
            dest[destIndex + 1] = texel.G;
            dest[destIndex + 2] = texel.B;
            dest[destIndex + 3] = texel.A;
        }

        return new ImportedTexture(
            $"embedded_{embeddedIndex}",
            width,
            height,
            4,
            IsSrgb(semantic),
            dest);
    }

    private static ImportedTexture LoadExternalTexture(string path, TextureSemantic semantic)
    {
        var data = File.ReadAllBytes(path);
        var image = ImageResult.FromMemory(data, ColorComponents.RedGreenBlueAlpha);

        return new ImportedTexture(
            path,
            image.Width,
            image.Height,
            (int)image.Comp,
            IsSrgb(semantic),
            image.Data);
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
            output.Add(new(node->MName.AsString, GetTransform(localTransform), mesh, materialIndex));
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

        return new(packed);
    }
}
