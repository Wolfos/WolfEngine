using System;
using System.Numerics;

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
}
