using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.AssetPipeline;

namespace WolfEngine;

[RuntimeAsset(AssetType.Material, typeof(MaterialAsset), typeof(IMaterialRuntimeAssetResolver))]
public sealed class Material
{
    public Material(string shaderPath)
    {
        if (string.IsNullOrWhiteSpace(shaderPath))
        {
            throw new ArgumentException("Shader path cannot be empty.", nameof(shaderPath));
        }

        ShaderPath = shaderPath;
    }

    public string ShaderPath { get; }
    

    public ColorRGBA Color { get; set; } = ColorRGBA.White;
    public float MetallicFactor { get; set; } = 1.0f;
    public float RoughnessFactor { get; set; } = 1.0f;
    public System.Numerics.Vector3 EmissiveFactor { get; set; } = System.Numerics.Vector3.Zero;
    public float EmissiveIntensity { get; set; } = 1.0f;
    public Texture AlbedoTexture { get; set; }
    public Texture MetallicRoughnessTexture { get; set; }
    public Texture NormalTexture { get; set; }
    public Texture EmissiveTexture { get; set; }
    public Texture OcclusionTexture { get; set; }
    
    public AlphaMode AlphaMode { get; set; }
    public float AlphaCutoff { get; set; }
    
    internal IMaterialResources Resources { get; set; }
}

public enum AlphaMode
{
    Opaque,
    AlphaTest,
    AlphaBlend
}
