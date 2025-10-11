using System.Text;
using Slangc.NET;

namespace WolfEngine;

public interface IShaderCompiler
{
	string GetMetalSource(string filename);
}

public class ShaderCompiler : IShaderCompiler
{
	private Dictionary<string, string> _cachedShaders = new();
	
	public string GetMetalSource(string filename)
	{
		if (string.IsNullOrWhiteSpace(filename))
		{
			throw new ArgumentException("Shader filename cannot be null or empty.", nameof(filename));
		}

		if (_cachedShaders.TryGetValue(filename, out var source)) return source;

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
		var metalSource = Encoding.UTF8.GetString(compiled);
		_cachedShaders.Add(filename, metalSource);
		return metalSource;
	}
}
