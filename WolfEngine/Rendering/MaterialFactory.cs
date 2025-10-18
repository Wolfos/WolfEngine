using System;

namespace WolfEngine;

public interface IMaterialFactory
{
	public Material GetMaterial(string shader);
}

public class MaterialFactory : IMaterialFactory
{
	public MaterialFactory(IShaderCompiler shaderCompiler)
	{
		if (shaderCompiler is null)
		{
			throw new ArgumentNullException(nameof(shaderCompiler));
		}
	}

	public Material GetMaterial(string shader)
	{
		if (string.IsNullOrWhiteSpace(shader))
		{
			throw new ArgumentException("Shader path cannot be empty.", nameof(shader));
		}

		return new Material(shader);
	}
}
