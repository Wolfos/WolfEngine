using System;
using System.Numerics;

namespace WolfEngine;

public sealed class Material
{
    public Material(IShaderCompiler shaderCompiler, string shaderPath)
    {
        if (shaderCompiler is null)
        {
            throw new ArgumentNullException(nameof(shaderCompiler));
        }

        if (string.IsNullOrWhiteSpace(shaderPath))
        {
            throw new ArgumentException("Shader path cannot be empty.", nameof(shaderPath));
        }

        ShaderPath = shaderPath;
        ShaderSource = shaderCompiler.GetMetalSource(shaderPath);
    }

    public string ShaderPath { get; }

    public string ShaderSource { get; }

    public Vector4 Color { get; set; }
}
