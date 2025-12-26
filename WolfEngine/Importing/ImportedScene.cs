using System.Numerics;
using WolfEngine.ECS;

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
    List<ImportedMaterial> Materials,
    List<ImportedTexture> Textures,
    List<ImportedMesh> Meshes
);

public record struct ImportedMaterial(
    Vector4 BaseColor,
    float MetallicFactor,
    float RoughnessFactor,
    Vector3 EmissiveFactor,
    int? BaseColorTextureIndex,
    int? NormalTextureIndex,
    int? MetallicRoughnessTextureIndex,
    int? OcclusionTextureIndex,
    int? EmissiveTextureIndex
);

public record struct ImportedTexture(
    string NameOrPath,
    int Width,
    int Height,
    int Channels,
    bool IsSrgb,
    byte[] PixelData
);

public record struct ImportedMesh(
    string Name,
    LocalTransform LocalTransform,
    Mesh Mesh,
    int MaterialIndex
);
