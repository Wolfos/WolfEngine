using System.Collections.Generic;
using Slangc.NET;

namespace WolfEngine;

public interface IShaderCompiler
{
	ReadOnlyMemory<byte> GetMetalLibrary(string filename);
	ReadOnlyMemory<byte> GetMetalLibrary(string filename, string vertexEntryPoint, string pixelEntryPoint, params string[] defines);
	ReadOnlyMemory<byte> GetMetalComputeLibrary(string filename, string entryPoint);
	byte[] GetDxil(string filename, string entryPoint, string profile, params string[] defines);
	ReadOnlyMemory<byte> GetComputeShader(string filename, string entryPoint);
}

public class ShaderCompiler : IShaderCompiler
{
	private readonly Dictionary<string, ReadOnlyMemory<byte>> _cachedMetalLibraries = new();
	private Dictionary<(string file, string entry, string profile, string defines), byte[]> _cachedDxil = new();

	public ReadOnlyMemory<byte> GetMetalLibrary(string filename)
	{
		return GetMetalLibrary(filename, "vertexShader", "fragmentShader");
	}

	public ReadOnlyMemory<byte> GetMetalLibrary(string filename, string vertexEntryPoint, string pixelEntryPoint, params string[] defines)
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
		if (_cachedMetalLibraries.TryGetValue(cacheKey, out var cachedLibrary))
		{
			return cachedLibrary;
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
			"-target", "metallib",
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

		var metalLibrary = SlangCompiler.Compile(args.ToArray());
		DumpMetalLibraryIfRequested(shaderPath, metalLibrary);
		_cachedMetalLibraries.Add(cacheKey, metalLibrary);
		return metalLibrary;
	}
	
	public ReadOnlyMemory<byte> GetMetalComputeLibrary(string filename, string entryPoint)
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

		var cacheKey = $"{filename}|cs={entryPoint}";
		if (_cachedMetalLibraries.TryGetValue(cacheKey, out var cachedLibrary))
		{
			return cachedLibrary;
		}

		var args = new[]
		{
			shaderPath,
			"-target", "metallib",
			"-D", "WOLF_BINDLESS_FIXED_SIZE=1",
			"-D", "WOLF_BINDLESS_MAX=16384",
			"-entry", entryPoint,
			"-stage", "compute",
			"-o", "-"
		};
		var argsWithDownstream = new List<string>(args);

		var metalLibrary = SlangCompiler.Compile(argsWithDownstream.ToArray());
		DumpMetalLibraryIfRequested(shaderPath, metalLibrary);
		_cachedMetalLibraries.Add(cacheKey, metalLibrary);
		return metalLibrary;
	}

	private static void DumpMetalLibraryIfRequested(string shaderPath, ReadOnlySpan<byte> metalLibrary)
	{
		var shouldDump = Environment.GetEnvironmentVariable("WOLF_DUMP_METALLIB") == "1" ||
		                 Environment.GetEnvironmentVariable("WOLF_DUMP_MSL") == "1";
		if (shouldDump == false)
		{
			return;
		}

		var fileName = Path.GetFileNameWithoutExtension(shaderPath) + ".metallib";
		var outputPath = Path.Combine(AppContext.BaseDirectory, "Shaders", fileName);
		File.WriteAllBytes(outputPath, metalLibrary.ToArray());
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
