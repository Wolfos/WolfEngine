using System;
using System.Collections.Generic;
using System.IO;
using Slangc.NET;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine;

public interface IShaderCompiler : IShaderProvider;

public class ShaderCompiler : IShaderCompiler
{
	private DevelopmentShaderProvider? _provider;
	private readonly Dictionary<string, ReadOnlyMemory<byte>> _cachedMetalLibraries = new();
	private readonly Dictionary<(string file, string entry, string profile, string defines), byte[]> _cachedDxil = new();
	private readonly Dictionary<(string file, string entry, string target, string profile, string stage, string defines), CompiledComputeShaderWithReflection> _cachedComputeWithReflection = new();
	private readonly Dictionary<(string file, string vsEntry, string psEntry, string target, string vsProfile, string psProfile, string defines), CompiledGraphicsShaderWithReflection> _cachedGraphicsWithReflection = new();

	public long Revision => EnsureProvider().Revision;
	public event Action<long>? RevisionChanged
	{
		add => EnsureProvider().RevisionChanged += value;
		remove => EnsureProvider().RevisionChanged -= value;
	}
	public CompiledShaderArtifact GetArtifact(ShaderRequest request) => EnsureProvider().GetArtifact(request);
	public void SetProjectRoot(string? projectRootPath) => EnsureProvider().SetProjectRoot(projectRootPath);
	public ShaderReloadResult Reload(GraphicsBackendKind backendKind) => EnsureProvider().Reload(backendKind);

	private DevelopmentShaderProvider EnsureProvider()
	{
		if (_provider is not null) return _provider;
		var configuredRoot = Environment.GetEnvironmentVariable("WOLF_ENGINE_CONTENT_ROOT");
		var root = string.IsNullOrWhiteSpace(configuredRoot)
			? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "WolfEngine"))
			: configuredRoot;
		_provider = new DevelopmentShaderProvider(new EngineShaderOptions { EngineContentRoot = root }, new EngineShaderCatalog());
		return _provider;
	}

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

		var shaderPath = ResolveShaderPath(filename);

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

		var compileArgs = args.ToArray();
		var metalLibrary = SlangCompiler.Compile(compileArgs);
		DumpMetalLibraryIfRequested(shaderPath, compileArgs, metalLibrary);
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

		var shaderPath = ResolveShaderPath(filename);

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
		DumpMetalLibraryIfRequested(shaderPath, args, metalLibrary);
		_cachedMetalLibraries.Add(cacheKey, metalLibrary);
		return metalLibrary;
	}

	private static void DumpMetalLibraryIfRequested(string shaderPath, IReadOnlyList<string> compileArgs, ReadOnlySpan<byte> metalLibrary)
	{
		var shouldDumpMetallib = Environment.GetEnvironmentVariable("WOLF_DUMP_METALLIB") == "1";
		var shouldDumpMsl = Environment.GetEnvironmentVariable("WOLF_DUMP_MSL") == "1";
		if (shouldDumpMetallib == false && shouldDumpMsl == false)
		{
			return;
		}

		var dumpBaseName = BuildMetalDumpBaseName(shaderPath, compileArgs);
		var shaderOutputDirectory = Path.Combine(Path.GetTempPath(), "WolfEngine", "ShaderDumps");
		Directory.CreateDirectory(shaderOutputDirectory);
		if (shouldDumpMetallib)
		{
			var metallibPath = Path.Combine(shaderOutputDirectory, $"{dumpBaseName}.metallib");
			File.WriteAllBytes(metallibPath, metalLibrary.ToArray());
		}

		if (shouldDumpMsl)
		{
			var metalSourceArgs = RewriteMetalCompileArgsForSource(compileArgs);
			var metalSource = SlangCompiler.Compile(metalSourceArgs);
			var metalSourcePath = Path.Combine(shaderOutputDirectory, $"{dumpBaseName}.metal");
			File.WriteAllBytes(metalSourcePath, metalSource.ToArray());
		}
	}

	private static string[] RewriteMetalCompileArgsForSource(IReadOnlyList<string> compileArgs)
	{
		var rewritten = new List<string>(compileArgs.Count);
		for (var i = 0; i < compileArgs.Count; i++)
		{
			var arg = compileArgs[i];
			if (arg == "-target" && i + 1 < compileArgs.Count)
			{
				rewritten.Add(arg);
				rewritten.Add("metal");
				i++;
				continue;
			}

			rewritten.Add(arg);
		}

		return rewritten.ToArray();
	}

	private static string BuildMetalDumpBaseName(string shaderPath, IReadOnlyList<string> compileArgs)
	{
		var baseName = Path.GetFileNameWithoutExtension(shaderPath);
		var entrySuffixParts = new List<string>();
		for (var i = 0; i < compileArgs.Count; i++)
		{
			if (compileArgs[i] != "-entry" || i + 1 >= compileArgs.Count)
			{
				continue;
			}

			entrySuffixParts.Add(SanitizeFileNamePart(compileArgs[i + 1]));
			i++;
		}

		if (entrySuffixParts.Count == 0)
		{
			return baseName;
		}

		return $"{baseName}.{string.Join(".", entrySuffixParts)}";
	}

	private static string SanitizeFileNamePart(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "unnamed";
		}

		var invalidChars = Path.GetInvalidFileNameChars();
		var sanitizedChars = value.ToCharArray();
		for (var i = 0; i < sanitizedChars.Length; i++)
		{
			if (Array.IndexOf(invalidChars, sanitizedChars[i]) >= 0)
			{
				sanitizedChars[i] = '_';
			}
		}

		return new string(sanitizedChars);
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

		var shaderPath = ResolveShaderPath(filename);

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

		var shaderPath = ResolveShaderPath(filename);

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
		var threadGroupSize = ResolveComputeThreadGroupSize(reflection, entryPoint);
		var result = new CompiledComputeShaderWithReflection(compiled, reflectionLayout, threadGroupSize);
		_cachedComputeWithReflection[cacheKey] = result;
		return result;
	}

	public CompiledGraphicsShaderWithReflection GetGraphicsShaderWithReflection(
		string filename,
		string vertexEntryPoint,
		string pixelEntryPoint,
		GraphicsBackendKind backendKind,
		params string[] defines)
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

		var shaderPath = ResolveShaderPath(filename);

		if (!File.Exists(shaderPath))
		{
			throw new FileNotFoundException($"Shader file '{shaderPath}' was not found.", shaderPath);
		}

		var normalizedDefines = BuildDefineSuffix(defines);
		var target = backendKind == GraphicsBackendKind.Metal ? "metallib" : "dxil";
		var vsProfile = backendKind == GraphicsBackendKind.Metal ? string.Empty : "vs_6_0";
		var psProfile = backendKind == GraphicsBackendKind.Metal ? string.Empty : "ps_6_0";
		var cacheKey = (filename, vertexEntryPoint, pixelEntryPoint, target, vsProfile, psProfile, normalizedDefines);
		if (_cachedGraphicsWithReflection.TryGetValue(cacheKey, out var cached))
		{
			return cached;
		}

		CompiledGraphicsShaderWithReflection compiledWithReflection;
		if (backendKind == GraphicsBackendKind.Metal)
		{
			var bytecode = GetMetalLibrary(filename, vertexEntryPoint, pixelEntryPoint, defines);
			var vertexLayout = CompileGraphicsStageReflection(shaderPath, backendKind, vertexEntryPoint, "vertex", string.Empty, defines);
			var pixelLayout = CompileGraphicsStageReflection(shaderPath, backendKind, pixelEntryPoint, "fragment", string.Empty, defines);
			var mergedLayout = ShaderReflectionLayoutMerger.Merge(
				vertexLayout.WithVisibility(ShaderStage.Vertex),
				pixelLayout.WithVisibility(ShaderStage.Pixel));
			compiledWithReflection = new CompiledGraphicsShaderWithReflection(
				new ShaderBytecodeSet(bytecode, bytecode),
				mergedLayout);
		}
		else
		{
			var vertexResult = CompileGraphicsStageWithReflection(shaderPath, backendKind, vertexEntryPoint, "vs_6_0", defines);
			var pixelResult = CompileGraphicsStageWithReflection(shaderPath, backendKind, pixelEntryPoint, "ps_6_0", defines);
			var mergedLayout = ShaderReflectionLayoutMerger.Merge(
				vertexResult.ReflectionLayout.WithVisibility(ShaderStage.Vertex),
				pixelResult.ReflectionLayout.WithVisibility(ShaderStage.Pixel));
			compiledWithReflection = new CompiledGraphicsShaderWithReflection(
				new ShaderBytecodeSet(vertexResult.Bytecode, pixelResult.Bytecode),
				mergedLayout);
		}

		_cachedGraphicsWithReflection[cacheKey] = compiledWithReflection;
		return compiledWithReflection;
	}

	private static ShaderReflectionLayout CompileGraphicsStageReflection(
		string shaderPath,
		GraphicsBackendKind backendKind,
		string entryPoint,
		string stage,
		string profile,
		params string[] defines)
	{
		var result = CompileGraphicsStageWithReflection(shaderPath, backendKind, entryPoint, profile, defines, stage);
		return result.ReflectionLayout;
	}

	private static (ReadOnlyMemory<byte> Bytecode, ShaderReflectionLayout ReflectionLayout) CompileGraphicsStageWithReflection(
		string shaderPath,
		GraphicsBackendKind backendKind,
		string entryPoint,
		string profile,
		string[] defines,
		string? explicitStage = null)
	{
		List<string> args;
		if (backendKind == GraphicsBackendKind.Metal)
		{
			var stage = explicitStage ?? throw new InvalidOperationException("Metal stage must be provided.");
			args =
			[
				shaderPath,
				"-target", "metallib",
				"-D", "WOLF_TARGET_METAL=1",
				"-D", "WOLF_BINDLESS_FIXED_SIZE=1",
				"-D", "WOLF_BINDLESS_MAX=16384",
				"-entry", entryPoint,
				"-stage", stage,
				"-o", "-"
			];
		}
		else
		{
			if (string.IsNullOrWhiteSpace(profile))
			{
				throw new InvalidOperationException("DX12 reflection compilation requires a shader profile.");
			}

			args =
			[
				shaderPath,
				"-target", "dxil",
				"-D", "WOLF_TARGET_D3D12=1",
				"-D", "WOLF_BINDLESS_FIXED_SIZE=1",
				"-D", "WOLF_BINDLESS_MAX=16384",
				"-D", "WOLF_BINDLESS_SAMPLER_MAX=2048",
				"-profile", profile,
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

		var bytecode = SlangCompiler.CompileWithReflection(args.ToArray(), out var reflection);
		return (bytecode, ShaderReflectionLayoutBuilder.Build(reflection));
	}

	private static ComputeThreadGroupSize ResolveComputeThreadGroupSize(SlangReflection reflection, string entryPoint)
	{
		var entryPoints = reflection.EntryPoints ?? [];
		for (var i = 0; i < entryPoints.Length; i++)
		{
			var candidate = entryPoints[i];
			if (string.Equals(candidate.Name, entryPoint, StringComparison.Ordinal) == false)
			{
				continue;
			}

			var size = candidate.ThreadGroupSize;
			if (size is not { Length: 3 } ||
			    size[0] == 0 ||
			    size[1] == 0 ||
			    size[2] == 0)
			{
				throw new InvalidOperationException(
					$"Reflected compute entry point '{entryPoint}' does not expose a valid 3D threadgroup size.");
			}

			return new ComputeThreadGroupSize(size[0], size[1], size[2]);
		}

		throw new InvalidOperationException(
			$"Reflected compute entry point '{entryPoint}' was not found when resolving threadgroup size.");
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

	private static string ResolveShaderPath(string filename)
	{
		if (Path.IsPathRooted(filename) == false)
			throw new ArgumentException("The low-level shader compiler requires an absolute source path.", nameof(filename));
		return Path.GetFullPath(filename);
	}
}
