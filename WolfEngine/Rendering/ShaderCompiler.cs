using System.Text;
using Slangc.NET;

namespace WolfEngine;

public interface IShaderCompiler
{
	string GetMetalSource(string filename);
	byte[] GetDxil(string filename, string entryPoint, string profile);
}

public class ShaderCompiler : IShaderCompiler
{
	private Dictionary<string, string> _cachedShaders = new();
	private Dictionary<(string file, string entry, string profile), byte[]> _cachedDxil = new();
	
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

	public byte[] GetDxil(string filename, string entryPoint, string profile)
	{
		if (string.IsNullOrWhiteSpace(filename))
		{
			throw new ArgumentException("Shader filename cannot be null or empty.", nameof(filename));
		}

		if (string.IsNullOrWhiteSpace(entryPoint))
		{
			throw new ArgumentException("Entry point cannot be null or empty.", nameof(entryPoint));
		}

		if (string.IsNullOrWhiteSpace(profile))
		{
			throw new ArgumentException("Profile cannot be null or empty.", nameof(profile));
		}

		var key = (filename, entryPoint, profile);
		if (_cachedDxil.TryGetValue(key, out var cached))
		{
			return cached;
		}

		var shaderPath = Path.IsPathRooted(filename)
			? filename
			: Path.Combine(AppContext.BaseDirectory, "Shaders", filename);

		if (!File.Exists(shaderPath))
		{
			throw new FileNotFoundException($"Shader file '{shaderPath}' was not found.", shaderPath);
		}


		var args = new List<string>
		{
			shaderPath,
			"-target", "dxil",
			"-profile", profile,
			"-entry", entryPoint,
			"-o", "-"
		};

		var compiled = SlangCompiler.Compile(args.ToArray());
		_cachedDxil.Add(key, compiled);
		return compiled;
	}
}
