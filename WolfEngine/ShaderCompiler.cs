using System;
using System.IO;
using System.Text;
using Slangc.NET;

namespace WolfEngine;

public interface IShaderCompiler
{
	string GetShader(string filename);
}

public class ShaderCompiler : IShaderCompiler
{
	public string GetShader(string filename)
	{
		if (string.IsNullOrWhiteSpace(filename))
		{
			throw new ArgumentException("Shader filename cannot be null or empty.", nameof(filename));
		}

		var shaderPath = Path.IsPathRooted(filename)
			? filename
			: Path.Combine(AppContext.BaseDirectory, "Shaders", filename);

		if (!File.Exists(shaderPath))
		{
			throw new FileNotFoundException($"Shader file '{shaderPath}' was not found.", shaderPath);
		}

		var args = new[]
		{
			shaderPath,
			"-target", "metal",
			"-entry", "vertexShader",
			"-stage", "vertex",
			"-entry", "fragmentShader",
			"-stage", "fragment",
			"-o", "-"
		};

		var compiled = SlangCompiler.Compile(args);
		return Encoding.UTF8.GetString(compiled);
	}
}
