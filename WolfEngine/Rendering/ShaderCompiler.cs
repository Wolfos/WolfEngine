using System.Collections.Generic;
using Slangc.NET;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine;

public interface IShaderCompiler
{
	ReadOnlyMemory<byte> GetMetalLibrary(string filename);
	ReadOnlyMemory<byte> GetMetalLibrary(string filename, string vertexEntryPoint, string pixelEntryPoint, params string[] defines);
	ReadOnlyMemory<byte> GetMetalComputeLibrary(string filename, string entryPoint);
	byte[] GetDxil(string filename, string entryPoint, string profile, params string[] defines);
	ReadOnlyMemory<byte> GetComputeShader(string filename, string entryPoint);
	CompiledComputeShaderWithReflection GetComputeShaderWithReflection(
		string filename,
		string entryPoint,
		GraphicsBackendKind backendKind,
		params string[] defines);
}

public class ShaderCompiler : IShaderCompiler
{
	private readonly Dictionary<string, ReadOnlyMemory<byte>> _cachedMetalLibraries = new();
	private readonly Dictionary<(string file, string entry, string profile, string defines), byte[]> _cachedDxil = new();
	private readonly Dictionary<(string file, string entry, string target, string profile, string stage, string defines), CompiledComputeShaderWithReflection> _cachedComputeWithReflection = new();

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
			"-D", "WOLF_TARGET_METAL=1",
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
			"-D", "WOLF_TARGET_METAL=1",
			"-D", "WOLF_BINDLESS_FIXED_SIZE=1",
			"-D", "WOLF_BINDLESS_MAX=16384",
			"-entry", entryPoint,
			"-stage", "compute",
			"-o", "-"
		};

		var metalLibrary = SlangCompiler.Compile(args);
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
			"-D", "WOLF_TARGET_D3D12=1",
			"-D", "WOLF_BINDLESS_FIXED_SIZE=1",
			"-D", "WOLF_BINDLESS_MAX=16384",
			"-D", "WOLF_BINDLESS_SAMPLER_MAX=2048",
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

	public CompiledComputeShaderWithReflection GetComputeShaderWithReflection(
		string filename,
		string entryPoint,
		GraphicsBackendKind backendKind,
		params string[] defines)
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

		var normalizedDefines = BuildDefineSuffix(defines);
		var target = backendKind == GraphicsBackendKind.Metal ? "metallib" : "dxil";
		var profile = backendKind == GraphicsBackendKind.Metal ? string.Empty : "cs_6_6";
		var stage = backendKind == GraphicsBackendKind.Metal ? "compute" : string.Empty;
		var cacheKey = (filename, entryPoint, target, profile, stage, normalizedDefines);
		if (_cachedComputeWithReflection.TryGetValue(cacheKey, out var cached))
		{
			return cached;
		}

		List<string> args;
		if (backendKind == GraphicsBackendKind.Metal)
		{
			args =
			[
				shaderPath,
				"-target", "metallib",
				"-D", "WOLF_TARGET_METAL=1",
				"-D", "WOLF_BINDLESS_FIXED_SIZE=1",
				"-D", "WOLF_BINDLESS_MAX=16384",
				"-entry", entryPoint,
				"-stage", "compute",
				"-o", "-"
			];
		}
		else
		{
			args =
			[
				shaderPath,
				"-target", "dxil",
				"-D", "WOLF_TARGET_D3D12=1",
				"-D", "WOLF_BINDLESS_FIXED_SIZE=1",
				"-D", "WOLF_BINDLESS_MAX=16384",
				"-D", "WOLF_BINDLESS_SAMPLER_MAX=2048",
				"-profile", "cs_6_6",
				"-entry", entryPoint,
				"-o", "-"
			];
		}

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

		var compiled = SlangCompiler.CompileWithReflection(args.ToArray(), out var reflection);
		var reflectionLayout = ShaderReflectionLayoutBuilder.Build(reflection);
		var result = new CompiledComputeShaderWithReflection(compiled, reflectionLayout);
		_cachedComputeWithReflection[cacheKey] = result;
		return result;
	}

	private static string BuildDefineSuffix(params string[] defines)
	{
		if (defines is not { Length: > 0 })
		{
			return string.Empty;
		}

		var normalized = new List<string>(defines.Length);
		for (var i = 0; i < defines.Length; i++)
		{
			if (string.IsNullOrWhiteSpace(defines[i]))
			{
				continue;
			}

			normalized.Add(defines[i]);
		}

		return normalized.Count == 0
			? string.Empty
			: string.Join(";", normalized);
	}
}
