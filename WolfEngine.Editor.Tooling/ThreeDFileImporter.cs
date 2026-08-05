using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.Rendering;
using System.Numerics;
using Silk.NET.Assimp;
using File = System.IO.File;
using AssimpTexture = Silk.NET.Assimp.Texture;
using AssimpMaterial = Silk.NET.Assimp.Material;
using AssimpAnimation = Silk.NET.Assimp.Animation;
using InvalidOperationException = System.InvalidOperationException;

namespace WolfEngine.Importing;

public class ThreeDFileImporter : IThreeDFileImporter
{
    /// <summary>
    /// <c>aiProcess_GlobalScale</c>. Silk.NET's <see cref="PostProcessSteps"/> enum stops at
    /// <see cref="PostProcessSteps.Debone"/> and never picked up the flags Assimp added after it, so
    /// the value is taken from <c>postprocess.h</c> directly.
    /// </summary>
    private const uint GlobalScalePostProcessStep = 0x8000000;

    /// <summary>
    /// <c>AI_CONFIG_GLOBAL_SCALE_FACTOR_KEY</c>. The scale step multiplies this with the file's own
    /// unit scale, which is how a centimetre-authored FBX arrives in metres.
    /// </summary>
    private const string GlobalScaleFactorProperty = "GLOBAL_SCALE_FACTOR";

    private const string FbxPreservePivotsProperty = "IMPORT_FBX_PRESERVE_PIVOTS";

    private readonly IImageLoader _imageLoader;

    public ThreeDFileImporter(IImageLoader imageLoader)
    {
        _imageLoader = imageLoader ?? throw new ArgumentNullException(nameof(imageLoader));
    }

    public unsafe ImportedScene Import(string filename, ModelImportSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        ArgumentNullException.ThrowIfNull(settings);

        var assimp = Assimp.GetApi();

        var fullPath = Path.IsPathRooted(filename)
            ? filename
            : Path.Combine(AppContext.BaseDirectory, filename);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Mesh file '{fullPath}' was not found.", fullPath);
        }

        var modelDirectory = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory;

        // LimitBoneWeights caps influences at four per vertex. Without it Assimp happily emits more,
        // and the extra influences would be silently dropped when packing the GPU skin attributes.
        // GlobalScale bakes the import scale into vertices, bone offsets and animation keys instead
        // of leaving a scale on the root node for every consumer of the hierarchy to reapply.
        const PostProcessSteps postProcessSteps = PostProcessSteps.Triangulate
                                                  | PostProcessSteps.JoinIdenticalVertices
                                                  | PostProcessSteps.CalculateTangentSpace
                                                  | PostProcessSteps.MakeLeftHanded
                                                  | PostProcessSteps.FlipWindingOrder
                                                  | PostProcessSteps.FlipUVs
                                                  | PostProcessSteps.LimitBoneWeights;
        var postProcess = (uint)postProcessSteps | GlobalScalePostProcessStep;
        var scaleFactor = settings.GetEffectiveScaleFactor();

        var scene = ImportWithProperties(assimp, fullPath, postProcess, scaleFactor, preserveFbxPivots: true);
        if (scene == null)
        {
            throw new InvalidOperationException($"Failed to load mesh from '{fullPath}'.");
        }

        if (RequiresPivotCollapse(scene))
        {
            scene = ReimportWithoutFbxPivots(assimp, scene, fullPath, postProcess, scaleFactor);
        }

        var nodes = new List<ImportedNode>();
        var materials = new List<ImportedMaterial>();
        var textures = new List<ImportedTexture>();
        var skeletons = new List<ImportedSkeleton>();
        var animations = new List<ImportedAnimation>();
        var meshData = new List<(string meshName, Mesh mesh, int materialIndex, int skeletonIndex)>();
        var textureLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Assimp's synthetic root node carries no useful name — FBX reports "RootNode" and glTF
        // whatever the exporter emitted ("ROOT", "Scene") — and that name ends up on the wrapper
        // entity a multi-root model is instantiated under. The file name is what the user recognises.
        var sceneName = Path.GetFileNameWithoutExtension(fullPath);

        try
        {
            // Materials (including texture references)
            for (var materialIndex = 0; materialIndex < scene->MNumMaterials; materialIndex++)
            {
                AssimpMaterial* material = scene->MMaterials[materialIndex];

                var baseColorVector = Vector4.One;
                assimp.GetMaterialColor(material, Assimp.MaterialColorDiffuseBase, 0, 0, ref baseColorVector);
                var baseColor = ColorRGBA.FromVector4(baseColorVector);

                var metallicFactor = GetMaterialFloat(assimp, material, Assimp.MatkeyMetallicFactor, 1.0f);
                var roughnessFactor = GetMaterialFloat(assimp, material, Assimp.MatkeyRoughnessFactor, 1.0f);
                var normalScale = GetMaterialScalar(
                    assimp,
                    material,
                    "$tex.scale",
                    1.0f,
                    clampToUnitRange: false,
                    type: (uint)TextureType.Normals);
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
                    textures, alphaMode == AlphaMode.Opaque ? TextureSemantic.BaseColor : TextureSemantic.BaseColorTransparent,
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
                    NormalScale: normalScale,
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

            // The skeleton has to exist before meshes are built, because a mesh's per-vertex bone
            // indices address skeleton bones rather than the mesh's own local bone list.
            var skeletonBuild = SkeletonBuilder.Build(scene);
            if (skeletonBuild is not null)
            {
                skeletons.Add(skeletonBuild.Skeleton);
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

                uint[] boneIndices = [];
                float[] boneWeights = [];
                var hasSkin = skeletonBuild is not null &&
                              SkinWeightPacker.TryPack(
                                  mesh,
                                  skeletonBuild.BoneIndicesByName,
                                  (int)vertexCount,
                                  meshName,
                                  out boneIndices,
                                  out boneWeights);

                var importedMesh = new Mesh(
                    vertices,
                    indexList,
                    rawNormals is not null ? normals : null,
                    hasTexCoords ? uvs : null,
                    tangents,
                    hasSkin ? boneIndices : null,
                    hasSkin ? boneWeights : null);
                meshData.Add((meshName, importedMesh, materialIndex, hasSkin ? 0 : -1));
            }

            // Traverse node graph to preserve hierarchy and local transforms. Bone nodes are folded
            // into the skeleton instead of becoming entities, so they are skipped here.
            BuildNodes(scene->MRootNode, meshData, nodes, skeletonBuild?.SkeletonNodeNames);

            if (skeletonBuild is not null || scene->MNumAnimations > 0)
            {
                AnimationConverter.Convert(scene, skeletonBuild, animations);
            }
        }
        finally
        {
            assimp.ReleaseImport(scene);
        }

        return new ImportedScene(sceneName, materials, textures, nodes, skeletons, animations);
    }

    /// <summary>
    /// Assimp's FBX reader splits each node's transform into synthetic <c>$AssimpFbx$</c> pivot
    /// nodes unless told otherwise. For a rig that is ruinous: a 65-bone Mixamo skeleton arrives as
    /// 176 bones named <c>mixamorig:Hips_$AssimpFbx$_PreRotation</c> and friends, which inflates
    /// every pose evaluation and, worse, replaces the portable bone names that clip binding and
    /// future retargeting are built on with importer-specific ones.
    /// </summary>
    /// <remarks>
    /// The setting has to be chosen before the file is parsed, and whether a file is rigged is only
    /// known after. Rather than change how every existing static FBX asset is laid out — which would
    /// renumber their sub-asset keys and break scene references — the collapse is applied only on a
    /// second pass, and only for files that actually contain a skin or an animation.
    /// </remarks>
    private static unsafe bool RequiresPivotCollapse(Scene* scene)
    {
        if (scene is null)
        {
            return false;
        }

        if (scene->MNumAnimations > 0)
        {
            return true;
        }

        for (var meshIndex = 0; meshIndex < scene->MNumMeshes; meshIndex++)
        {
            var mesh = scene->MMeshes[meshIndex];
            if (mesh is not null && mesh->MNumBones > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static unsafe Scene* ReimportWithoutFbxPivots(
        Assimp assimp,
        Scene* original,
        string fullPath,
        uint postProcess,
        float scaleFactor)
    {
        var collapsed = ImportWithProperties(assimp, fullPath, postProcess, scaleFactor, preserveFbxPivots: false);
        if (collapsed is null)
        {
            // Keep the pivot-preserving scene rather than failing the import outright.
            return original;
        }

        assimp.ReleaseImport(original);
        return collapsed;
    }

    /// <summary>
    /// Every import goes through a property store because the scale factor is a configuration
    /// property rather than a post-process flag; <c>aiProcess_GlobalScale</c> on its own would only
    /// apply the source file's unit conversion.
    /// </summary>
    private static unsafe Scene* ImportWithProperties(
        Assimp assimp,
        string fullPath,
        uint postProcess,
        float scaleFactor,
        bool preserveFbxPivots)
    {
        var propertyStore = assimp.CreatePropertyStore();
        if (propertyStore is null)
        {
            throw new InvalidOperationException($"Failed to allocate Assimp import properties for '{fullPath}'.");
        }

        try
        {
            assimp.SetImportPropertyFloat(propertyStore, GlobalScaleFactorProperty, scaleFactor);
            if (preserveFbxPivots == false)
            {
                assimp.SetImportPropertyInteger(propertyStore, FbxPreservePivotsProperty, 0);
            }

            return assimp.ImportFileExWithProperties(fullPath, postProcess, null, propertyStore);
        }
        finally
        {
            assimp.ReleasePropertyStore(propertyStore);
        }
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
        bool clampToUnitRange,
        uint type = 0,
        uint index = 0)
    {
        float value = defaultValue;
        uint max = 1;
        var result = assimp.GetMaterialFloatArray(material, key, type, index, ref value, ref max);
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
            StbImageLoader.IsSrgb(semantic),
            semantic,
            [new Rendering.TextureMipData(width, height, dest)]);
    }

    private static unsafe void BuildNodes(
        Node* root,
        IReadOnlyList<(string meshName, Mesh mesh, int materialIndex, int skeletonIndex)> meshData,
        List<ImportedNode> output,
        IReadOnlySet<string>? skeletonNodeNames)
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

                AppendNode(
                    child,
                    meshData,
                    output,
                    parentIndex: -1,
                    fallbackName: $"Node_{childIndex}",
                    skeletonNodeNames);
            }

            return;
        }

        AppendNode(root, meshData, output, parentIndex: -1, fallbackName: "Node_0", skeletonNodeNames);
    }

    /// <summary>
    /// A bone becomes part of the <see cref="ImportedSkeleton"/> rather than an entity, so its node
    /// is dropped from the hierarchy. The subtree check keeps geometry parented under a bone — a
    /// weapon on a hand bone, say — from disappearing along with the bone chain.
    /// </summary>
    private static unsafe bool ShouldSkipSkeletonNode(Node* node, IReadOnlySet<string>? skeletonNodeNames)
    {
        if (skeletonNodeNames is null || node is null)
        {
            return false;
        }

        return skeletonNodeNames.Contains(node->MName.AsString) && SubtreeHasMeshes(node) == false;
    }

    private static unsafe bool SubtreeHasMeshes(Node* node)
    {
        if (node is null)
        {
            return false;
        }

        if (node->MNumMeshes > 0)
        {
            return true;
        }

        for (var i = 0; i < node->MNumChildren; i++)
        {
            if (SubtreeHasMeshes(node->MChildren[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static unsafe bool ShouldTreatChildrenAsRoots(Node* root)
    {
        if (root is null || root->MNumMeshes > 0 || root->MNumChildren == 0)
        {
            return false;
        }

        return IsApproximatelyIdentity(GetTransform(root->MTransformation));
    }

    private static unsafe void AppendNode(
        Node* node,
        IReadOnlyList<(string meshName, Mesh mesh, int materialIndex, int skeletonIndex)> meshData,
        List<ImportedNode> output,
        int parentIndex,
        string fallbackName,
        IReadOnlySet<string>? skeletonNodeNames)
    {
        if (node is null)
        {
            return;
        }

        if (ShouldSkipSkeletonNode(node, skeletonNodeNames))
        {
            return;
        }

        var meshes = new List<ImportedNodeMesh>((int)node->MNumMeshes);

        for (var i = 0; i < node->MNumMeshes; i++)
        {
            var meshIndex = (int)node->MMeshes[i];
            if (meshIndex < 0 || meshIndex >= meshData.Count)
            {
                continue;
            }

            var (meshName, mesh, materialIndex, skeletonIndex) = meshData[meshIndex];
            meshes.Add(new ImportedNodeMesh(meshName, mesh, materialIndex, skeletonIndex));
        }

        var nodeName = string.IsNullOrWhiteSpace(node->MName.AsString) ? fallbackName : node->MName.AsString;
        var nodeIndex = output.Count;
        output.Add(new ImportedNode(nodeName, GetTransform(node->MTransformation), meshes, parentIndex));

        for (var childIndex = 0; childIndex < node->MNumChildren; childIndex++)
        {
            var child = node->MChildren[childIndex];
            if (child is null)
            {
                continue;
            }

            AppendNode(
                child,
                meshData,
                output,
                nodeIndex,
                $"{fallbackName}_{childIndex}",
                skeletonNodeNames);
        }
    }

    internal static Matrix4x4 ConvertTransform(Matrix4x4 assimpMatrix) => GetTransform(assimpMatrix);

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
