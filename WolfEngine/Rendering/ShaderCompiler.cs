using System.Text;
using System.Text.RegularExpressions;
using Slangc.NET;

namespace WolfEngine;

public interface IShaderCompiler
{
	string GetMetalSource(string filename);
	string GetMetalComputeSource(string filename, string entryPoint);
	byte[] GetDxil(string filename, string entryPoint, string profile);
	ReadOnlyMemory<byte> GetComputeShader(string filename, string entryPoint);
}

public class ShaderCompiler : IShaderCompiler
{
	private Dictionary<string, string> _cachedShaders = new();
	private Dictionary<(string file, string entry, string profile), byte[]> _cachedDxil = new();

	private static string InjectArgumentBufferIds(string source)
	{
		source = Regex.Replace(
			source,
			@"array<sampler, int\((\d+)\)>\s+(g_Samplers_\d+);",
			"array<sampler, int($1)> $2 [[id(0)]];");
		source = Regex.Replace(
			source,
			@"array<texture2d<float, access::sample>, int\((\d+)\)>\s+(g_Textures_\d+);",
			"array<texture2d<float, access::sample>, int($1)> $2 [[id(0)]];");
		source = Regex.Replace(
			source,
			@"array<texture2d<float, access::read_write>, int\((\d+)\)>\s+(g_RWTextures_\d+);",
			"array<texture2d<float, access::read_write>, int($1)> $2 [[id(0)]];");
		return source;
	}
	
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
			"-D", "WOLF_BINDLESS_FIXED_SIZE=1",
			"-D", "WOLF_BINDLESS_MAX=16384",
			"-entry", "vertexShader",
			"-stage", "vertex",
			"-entry", "fragmentShader",
			"-stage", "fragment",
			"-o", "-"
		};

		var compiled = SlangCompiler.Compile(args);
		var metalSource = InjectArgumentBufferIds(Encoding.UTF8.GetString(compiled));
		_cachedShaders.Add(filename, metalSource);
		return metalSource;
	}
	
	public string GetMetalComputeSource(string filename, string entryPoint)
	{
		if (string.IsNullOrWhiteSpace(filename))
		{
			throw new ArgumentException("Shader filename cannot be null or empty.", nameof(filename));
		}

		if (string.IsNullOrWhiteSpace(entryPoint))
		{
			throw new ArgumentException("Entry point cannot be null or empty.", nameof(entryPoint));
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
			"-D", "WOLF_BINDLESS_FIXED_SIZE=1",
			"-D", "WOLF_BINDLESS_MAX=16384",
			"-entry", entryPoint,
			"-stage", "compute",
			"-o", "-"
		};

		var compiled = SlangCompiler.Compile(args);
		return InjectArgumentBufferIds(Encoding.UTF8.GetString(compiled));
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

	public ReadOnlyMemory<byte> GetComputeShader(string filename, string entryPoint)
	{
		return GetDxil(filename, entryPoint, "cs_6_6");
	}

}
