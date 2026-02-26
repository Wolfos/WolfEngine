using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Slangc.NET;

namespace WolfEngine;

public interface IShaderCompiler
{
	string GetMetalSource(string filename);
	string GetMetalSource(string filename, string vertexEntryPoint, string pixelEntryPoint, params string[] defines);
	string GetMetalComputeSource(string filename, string entryPoint);
	byte[] GetDxil(string filename, string entryPoint, string profile, params string[] defines);
	ReadOnlyMemory<byte> GetComputeShader(string filename, string entryPoint);
}

public class ShaderCompiler : IShaderCompiler
{
	private readonly Dictionary<string, string> _cachedShaders = new();
	private Dictionary<(string file, string entry, string profile, string defines), byte[]> _cachedDxil = new();

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
		return GetMetalSource(filename, "vertexShader", "fragmentShader");
	}

	public string GetMetalSource(string filename, string vertexEntryPoint, string pixelEntryPoint, params string[] defines)
	{
		if (string.IsNullOrWhiteSpace(filename))
		{
			throw new ArgumentException("Shader filename cannot be null or empty.", nameof(filename));
		}

		if (string.IsNullOrWhiteSpace(vertexEntryPoint))
		{
			throw new ArgumentException("Vertex entry point cannot be null or empty.", nameof(vertexEntryPoint));
		}

		if (string.IsNullOrWhiteSpace(pixelEntryPoint))
		{
			throw new ArgumentException("Pixel entry point cannot be null or empty.", nameof(pixelEntryPoint));
		}

		var definesSuffix = defines is { Length: > 0 }
			? string.Join(";", defines)
			: string.Empty;
		var cacheKey = $"{filename}|vs={vertexEntryPoint}|ps={pixelEntryPoint}|defs={definesSuffix}";
		if (_cachedShaders.TryGetValue(cacheKey, out var source))
		{
			return source;
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
			"-target", "metal",
			"-D", "WOLF_BINDLESS_FIXED_SIZE=1",
			"-D", "WOLF_BINDLESS_MAX=16384",
			"-entry", vertexEntryPoint,
			"-stage", "vertex",
			"-entry", pixelEntryPoint,
			"-stage", "fragment",
			"-o", "-"
		};

		if (defines is { Length: > 0 })
		{
			for (var i = 0; i < defines.Length; i++)
			{
				if (string.IsNullOrWhiteSpace(defines[i]))
				{
					continue;
				}

				args.Add("-D");
				args.Add(defines[i]);
			}
		}

		var compiled = SlangCompiler.Compile(args.ToArray());
		var metalSource = InjectArgumentBufferIds(Encoding.UTF8.GetString(compiled));
		DumpMetalSourceIfRequested(shaderPath, metalSource);
		_cachedShaders.Add(cacheKey, metalSource);
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
		var metalSource = InjectArgumentBufferIds(Encoding.UTF8.GetString(compiled));
		DumpMetalSourceIfRequested(shaderPath, metalSource);
		return metalSource;
	}

	private static void DumpMetalSourceIfRequested(string shaderPath, string metalSource)
	{
		if (Environment.GetEnvironmentVariable("WOLF_DUMP_MSL") != "1")
		{
			return;
		}

		var fileName = Path.GetFileNameWithoutExtension(shaderPath) + ".metal.msl";
		var outputPath = Path.Combine(AppContext.BaseDirectory, "Shaders", fileName);
		File.WriteAllText(outputPath, metalSource);
	}


	public byte[] GetDxil(string filename, string entryPoint, string profile, params string[] defines)
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

		var definesSuffix = defines is { Length: > 0 }
			? string.Join(";", defines)
			: string.Empty;
		var key = (filename, entryPoint, profile, definesSuffix);
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
		if (defines is { Length: > 0 })
		{
			for (var i = 0; i < defines.Length; i++)
			{
				if (string.IsNullOrWhiteSpace(defines[i]))
				{
					continue;
				}

				args.Add("-D");
				args.Add(defines[i]);
			}
		}

		var compiled = SlangCompiler.Compile(args.ToArray());
		_cachedDxil.Add(key, compiled);
		return compiled;
	}

	public ReadOnlyMemory<byte> GetComputeShader(string filename, string entryPoint)
	{
		return GetDxil(filename, entryPoint, "cs_6_6");
	}

}
