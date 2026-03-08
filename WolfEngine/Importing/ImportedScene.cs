using System.Numerics;

namespace WolfEngine.Importing;

public enum TextureSemantic
{
    Unknown = 0,
    BaseColor,
    Normal,
    MetallicRoughness,
    Occlusion,
    Emissive
}

public record ImportedScene(
    string Name,
    List<ImportedMaterial> Materials,
    List<ImportedTexture> Textures,
    List<ImportedNode> RootNodes
);

public record struct ImportedMaterial(
    Vector4 BaseColor,
    float MetallicFactor,
    float RoughnessFactor,
    Vector3 EmissiveFactor,
    float EmissiveIntensity,
    int? BaseColorTextureIndex,
    int? NormalTextureIndex,
    int? MetallicRoughnessTextureIndex,
    int? OcclusionTextureIndex,
    int? EmissiveTextureIndex,
    AlphaMode AlphaMode,
    float AlphaCutoff
);

public record struct ImportedTexture(
    string NameOrPath,
    int Width,
    int Height,
    int Channels,
    bool IsSrgb,
    byte[] PixelData
);

public record ImportedNode(
    string Name,
    Matrix4x4 LocalTransform,
    List<ImportedNodeMesh> Meshes,
    List<ImportedNode> Children
);

public record struct ImportedNodeMesh(
    string Name,
    Mesh Mesh,
    int MaterialIndex
);
