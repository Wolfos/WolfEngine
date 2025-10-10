using System;
using System.Numerics;

namespace WolfEngine;

public sealed class Material
{
    public Material(IShaderCompiler shaderCompiler, string shaderPath, Vector4 color)
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
        Color = color;
        ShaderSource = shaderCompiler.GetShader(shaderPath);
    }

    public string ShaderPath { get; }

    public string ShaderSource { get; }

    public Vector4 Color { get; }
}
