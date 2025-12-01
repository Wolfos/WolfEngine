using System.Numerics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine;

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

    public Vector4 Color { get; set; }
    public Texture? AlbedoTexture { get; set; }
    public Texture? MetallicRoughnessTexture { get; set; }
    public Texture? NormalTexture { get; set; }
    public Texture? EmissiveTexture { get; set; }
    public Texture? OcclusionTexture { get; set; }
    
    internal IMaterialResources Resources { get; set; }
}
