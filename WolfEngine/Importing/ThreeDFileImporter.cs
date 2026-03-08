using System.Numerics;
using Silk.NET.Assimp;
using WolfEngine;
using File = System.IO.File;
using AssimpTexture = Silk.NET.Assimp.Texture;
using AssimpMaterial = Silk.NET.Assimp.Material;
using InvalidOperationException = System.InvalidOperationException;

namespace WolfEngine.Importing;

public class ThreeDFileImporter : IThreeDFileImporter
{
    private readonly IImageLoader _imageLoader;

    public ThreeDFileImporter(IImageLoader imageLoader)
    {
        _imageLoader = imageLoader ?? throw new ArgumentNullException(nameof(imageLoader));
    }

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
                                             | PostProcessSteps.CalculateTangentSpace
                                             | PostProcessSteps.MakeLeftHanded
                                             | PostProcessSteps.FlipWindingOrder
                                             | PostProcessSteps.FlipUVs;

        var scene = assimp.ImportFile(fullPath, (uint)postProcess);
        if (scene == null)
        {
            throw new InvalidOperationException($"Failed to load mesh from '{fullPath}'.");
        }

        var rootNodes = new List<ImportedNode>();
        var materials = new List<ImportedMaterial>();
        var textures = new List<ImportedTexture>();
        var meshData = new List<(string meshName, Mesh mesh, int materialIndex)>();
        var textureLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sceneName = string.IsNullOrWhiteSpace(scene->MRootNode->MName.AsString)
            ? Path.GetFileNameWithoutExtension(fullPath)
            : scene->MRootNode->MName.AsString;

        try
        {
            // Materials (including texture references)
            for (var materialIndex = 0; materialIndex < scene->MNumMaterials; materialIndex++)
            {
                AssimpMaterial* material = scene->MMaterials[materialIndex];

                var baseColor = Vector4.One;
                assimp.GetMaterialColor(material, Assimp.MaterialColorDiffuseBase, 0, 0, ref baseColor);

                var metallicFactor = GetMaterialFloat(assimp, material, Assimp.MatkeyMetallicFactor, 1.0f);
                var roughnessFactor = GetMaterialFloat(assimp, material, Assimp.MatkeyRoughnessFactor, 1.0f);
                var emissiveIntensity = GetMaterialScalar(assimp, material, Assimp.MatkeyEmissiveIntensity, 1.0f, clampToUnitRange: false);

                var aMode = GetMaterialString(assimp, material, "$mat.gltf.alphaMode", "OPAQUE");
                var alphaCutoff = aMode == "MASK"
                    ? GetMaterialFloat(assimp, material, "$mat.gltf.alphaCutoff", 0.5f)
                    : 0.0f; // or ignore for non-MASK

                AlphaMode alphaMode = AlphaMode.Opaque;
                switch (aMode)
                {
                    case "OPAQUE":
                        alphaMode = AlphaMode.Opaque;
                        break;
                    case "MASK":
                        alphaMode = AlphaMode.AlphaTest;
                        break;
                    case "BLEND":
                        alphaMode = AlphaMode.AlphaBlend;
                        break;
                }
                
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

                var emissiveFactor = TryGetMaterialVector3(assimp, material, Assimp.MaterialColorEmissiveBase, out var importedEmissiveFactor)
                    ? importedEmissiveFactor
                    : emissiveTextureIndex is not null
                        ? Vector3.One
                        : Vector3.Zero;

                materials.Add(new ImportedMaterial(
                    baseColor,
                    MetallicFactor: metallicFactor,
                    RoughnessFactor: roughnessFactor,
                    EmissiveFactor: emissiveFactor,
                    EmissiveIntensity: Math.Max(0.0f, emissiveIntensity),
                    BaseColorTextureIndex: baseColorTextureIndex,
                    NormalTextureIndex: normalTextureIndex,
                    MetallicRoughnessTextureIndex: metallicRoughnessTextureIndex,
                    OcclusionTextureIndex: occlusionTextureIndex,
                    EmissiveTextureIndex: emissiveTextureIndex,
                    AlphaMode: alphaMode,
                    AlphaCutoff: alphaCutoff
                    ));
            }

            // Mesh data (geometry + material index)
            for (var meshIndex = 0; meshIndex < scene->MNumMeshes; meshIndex++)
            {
                var mesh = scene->MMeshes[meshIndex];

                var vertexCount = mesh->MNumVertices;
                var vertices = new Vector4[vertexCount];
                var normals = new Vector3[vertexCount];
                var uvs = new Vector2[vertexCount];
                var tangents = new Vector4[vertexCount];
                var rawVertices = mesh->MVertices;
                var rawNormals = mesh->MNormals;
                var rawTangents = mesh->MTangents;
                var rawBitangents = mesh->MBitangents;
                var texCoords0 = mesh->MTextureCoords[0];
                var hasTexCoords = texCoords0 is not null;
                for (var i = 0; i < vertexCount; i++)
                {
                    var position = rawVertices[i];
                    vertices[i] = new(position.X, position.Y, position.Z, 1.0f);
                    if (rawNormals is not null)
                    {
                        var normal = rawNormals[i];
                        normals[i] = Vector3.Normalize(new(normal.X, normal.Y, normal.Z));
                    }

                    if (hasTexCoords)
                    {
                        var texCoord = texCoords0[i];
                        uvs[i] = new(texCoord.X, texCoord.Y);
                    }

                    if (rawTangents is not null && rawBitangents is not null && rawNormals is not null)
                    {
                        var tangent = rawTangents[i];
                        var bitangent = rawBitangents[i];
                        var normal = rawNormals[i];
                        var t = new Vector3(tangent.X, tangent.Y, tangent.Z);
                        var n = new Vector3(normal.X, normal.Y, normal.Z);
                        var b = new Vector3(bitangent.X, bitangent.Y, bitangent.Z);
                        var handedness = Vector3.Dot(Vector3.Cross(n, t), b) < 0.0f ? -1.0f : 1.0f;
                        tangents[i] = new(t.X, t.Y, t.Z, handedness);
                    }
                    else
                    {
                        tangents[i] = new Vector4(1, 0, 0, 1);
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
                var meshName = string.IsNullOrWhiteSpace(mesh->MName.AsString)
                    ? $"Mesh_{meshIndex}"
                    : mesh->MName.AsString;
                var importedMesh = new Mesh(
                    vertices,
                    indexList,
                    rawNormals is not null ? normals : null,
                    hasTexCoords ? uvs : null,
                    tangents);
                meshData.Add((meshName, importedMesh, materialIndex));
            }

            // Traverse node graph to preserve hierarchy and local transforms.
            BuildRootNodes(scene->MRootNode, meshData, rootNodes);
        }
        finally
        {
            assimp.ReleaseImport(scene);
        }

        return new ImportedScene(sceneName, materials, textures, rootNodes);
    }

    private static bool IsSrgb(TextureSemantic semantic) => semantic is TextureSemantic.BaseColor or TextureSemantic.Emissive;

    private static unsafe float GetMaterialFloat(Assimp assimp, AssimpMaterial* material, string key, float defaultValue)
        => GetMaterialScalar(assimp, material, key, defaultValue, clampToUnitRange: true);

    private static unsafe bool TryGetMaterialVector3(
        Assimp assimp,
        AssimpMaterial* material,
        string key,
        out Vector3 value)
    {
        value = Vector3.Zero;
        Span<float> components = stackalloc float[4];
        uint max = 4;
        var result = assimp.GetMaterialFloatArray(material, key, 0, 0, ref components[0], ref max);
        if (result != Return.Success || max == 0)
        {
            return false;
        }

        value = new Vector3(
            max > 0 ? components[0] : 0.0f,
            max > 1 ? components[1] : 0.0f,
            max > 2 ? components[2] : 0.0f);
        return true;
    }

    private static unsafe float GetMaterialScalar(
        Assimp assimp,
        AssimpMaterial* material,
        string key,
        float defaultValue,
        bool clampToUnitRange)
    {
        float value = defaultValue;
        uint max = 1;
        var result = assimp.GetMaterialFloatArray(material, key, 0, 0, ref value, ref max);
        if (result != Return.Success || max == 0)
        {
            return defaultValue;
        }
        return clampToUnitRange ? Math.Clamp(value, 0.0f, 1.0f) : value;
    }

    private static unsafe string GetMaterialString(
        Assimp assimp,
        AssimpMaterial* material,
        string key,
        string defaultValue,
        uint type = 0,
        uint index = 0)
    {
        AssimpString value = default;
        var result = assimp.GetMaterialString(material, key, type, index, ref value);
        return result == Return.Success ? value.AsString : defaultValue;
    }
    
    private static unsafe int GetMaterialInt(Assimp assimp, AssimpMaterial* material, string key, int defaultValue)
    {
        int value = defaultValue;
        uint max = 1;
        var result = assimp.GetMaterialIntegerArray(material, key, 0, 0, ref value, ref max);
        if (result != Return.Success || max == 0)
        {
            return defaultValue;
        }
        return value;
    }

    private unsafe int? TryLoadMaterialTexture(
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

            try
            {
                var index = GetOrLoadTextureIndex(scene, texturePath, semantic, modelDirectory, textureLookup,
                    textures);
                return index;
            }
            catch (Exception e)
            {
                Console.Out.WriteLine($"Error loading texture {texturePath}");
                Console.Out.WriteLine(e.Message);
                return 0;
            }
        }

        return null;
    }

    private unsafe int GetOrLoadTextureIndex(
        Scene* scene,
        string texturePath,
        TextureSemantic semantic,
        string modelDirectory,
        Dictionary<string, int> textureLookup,
        List<ImportedTexture> textures)
    {
        var isEmbedded = texturePath.Length > 0 && texturePath[0] == '*';
        var normalizedTexturePath = isEmbedded ? texturePath : NormalizeRelativePath(texturePath);
        var key = isEmbedded
            ? texturePath
            : Path.GetFullPath(
                Path.IsPathRooted(normalizedTexturePath)
                    ? normalizedTexturePath
                    : Path.Combine(modelDirectory, normalizedTexturePath));

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

            importedTexture = _imageLoader.Load(key, semantic);
        }

        var index = textures.Count;
        textures.Add(importedTexture);
        textureLookup[key] = index;
        return index;
    }

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var normalized = path.Replace('\\', Path.DirectorySeparatorChar);
        return normalized.Replace('/', Path.DirectorySeparatorChar);
    }

    private unsafe ImportedTexture LoadEmbeddedTexture(Scene* scene, string texturePath, TextureSemantic semantic)
    {
        if (texturePath.Length < 2 || !int.TryParse(texturePath.AsSpan(1), out var embeddedIndex))
        {
            throw new InvalidOperationException($"Invalid embedded texture reference '{texturePath}'.");
        }

        if (embeddedIndex < 0 || embeddedIndex >= scene->MNumTextures)
        {
            throw new InvalidOperationException($"Embedded texture index {embeddedIndex} is out of range.");
        }

        AssimpTexture* texture = scene->MTextures[embeddedIndex];
        if (texture == null)
        {
            throw new InvalidOperationException($"Embedded texture {embeddedIndex} was null.");
        }

        return texture->MHeight == 0
            ? LoadCompressedEmbeddedTexture(texture, embeddedIndex, semantic)
            : LoadRawEmbeddedTexture(texture, embeddedIndex, semantic);
    }

    private unsafe ImportedTexture LoadCompressedEmbeddedTexture(AssimpTexture* texture, int embeddedIndex, TextureSemantic semantic)
    {
        var byteLength = checked((int)texture->MWidth);
        var data = new byte[byteLength];
        var raw = (byte*)texture->PcData;
        var source = new ReadOnlySpan<byte>(raw, byteLength);
        source.CopyTo(data);

        return _imageLoader.LoadEmbedded(data, semantic, $"embedded_{embeddedIndex}");
    }

    private unsafe ImportedTexture LoadRawEmbeddedTexture(AssimpTexture* texture, int embeddedIndex, TextureSemantic semantic)
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
            StbImageLoader.IsSrgb(semantic),
            dest);
    }

    private static unsafe void BuildRootNodes(
        Node* root,
        IReadOnlyList<(string meshName, Mesh mesh, int materialIndex)> meshData,
        List<ImportedNode> output)
    {
        if (root is null)
        {
            return;
        }

        if (ShouldTreatChildrenAsRoots(root))
        {
            for (var childIndex = 0; childIndex < root->MNumChildren; childIndex++)
            {
                var child = root->MChildren[childIndex];
                if (child is null)
                {
                    continue;
                }

                output.Add(BuildNodeRecursive(
                    child,
                    meshData,
                    $"Node_{childIndex}"));
            }

            return;
        }

        output.Add(BuildNodeRecursive(root, meshData, "Node_0"));
    }

    private static unsafe bool ShouldTreatChildrenAsRoots(Node* root)
    {
        if (root is null || root->MNumMeshes > 0 || root->MNumChildren == 0)
        {
            return false;
        }

        return IsApproximatelyIdentity(GetTransform(root->MTransformation));
    }

    private static unsafe ImportedNode BuildNodeRecursive(
        Node* node,
        IReadOnlyList<(string meshName, Mesh mesh, int materialIndex)> meshData,
        string fallbackName)
    {
        if (node is null)
        {
            return new ImportedNode(
                fallbackName,
                Matrix4x4.Identity,
                new List<ImportedNodeMesh>(),
                new List<ImportedNode>());
        }

        var meshes = new List<ImportedNodeMesh>((int)node->MNumMeshes);

        for (var i = 0; i < node->MNumMeshes; i++)
        {
            var meshIndex = (int)node->MMeshes[i];
            if (meshIndex < 0 || meshIndex >= meshData.Count)
            {
                continue;
            }

            var (meshName, mesh, materialIndex) = meshData[meshIndex];
            meshes.Add(new ImportedNodeMesh(meshName, mesh, materialIndex));
        }

        var children = new List<ImportedNode>((int)node->MNumChildren);
        for (var childIndex = 0; childIndex < node->MNumChildren; childIndex++)
        {
            var child = node->MChildren[childIndex];
            if (child is null)
            {
                continue;
            }

            children.Add(BuildNodeRecursive(
                child,
                meshData,
                $"{fallbackName}_{childIndex}"));
        }

        var nodeName = string.IsNullOrWhiteSpace(node->MName.AsString) ? fallbackName : node->MName.AsString;
        return new ImportedNode(nodeName, GetTransform(node->MTransformation), meshes, children);
    }

    private static bool IsApproximatelyIdentity(Matrix4x4 matrix, float epsilon = 0.0001f)
    {
        return
            MathF.Abs(matrix.M11 - 1.0f) <= epsilon &&
            MathF.Abs(matrix.M22 - 1.0f) <= epsilon &&
            MathF.Abs(matrix.M33 - 1.0f) <= epsilon &&
            MathF.Abs(matrix.M44 - 1.0f) <= epsilon &&
            MathF.Abs(matrix.M12) <= epsilon &&
            MathF.Abs(matrix.M13) <= epsilon &&
            MathF.Abs(matrix.M14) <= epsilon &&
            MathF.Abs(matrix.M21) <= epsilon &&
            MathF.Abs(matrix.M23) <= epsilon &&
            MathF.Abs(matrix.M24) <= epsilon &&
            MathF.Abs(matrix.M31) <= epsilon &&
            MathF.Abs(matrix.M32) <= epsilon &&
            MathF.Abs(matrix.M34) <= epsilon &&
            MathF.Abs(matrix.M41) <= epsilon &&
            MathF.Abs(matrix.M42) <= epsilon &&
            MathF.Abs(matrix.M43) <= epsilon;
    }

    private static Matrix4x4 GetTransform(Matrix4x4 m)
    {
        // Convert Assimp matrix to column major
        return new Matrix4x4(
            m.M11, m.M21, m.M31, m.M41,
            m.M12, m.M22, m.M32, m.M42,
            m.M13, m.M23, m.M33, m.M43,
            m.M14, m.M24, m.M34, m.M44);
    }
}
