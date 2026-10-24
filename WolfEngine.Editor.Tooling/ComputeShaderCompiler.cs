using System.Security.Cryptography;
using System.Text.RegularExpressions;
using WolfEngine.Importing;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Editor.Tooling;

/// <summary>
/// Compiles a game's <c>.compute.slang</c> file. Kernels are the functions marked <c>[shader("compute")]</c>, and
/// the source can <c>import wolf;</c>, the engine's public shader module.
/// </summary>
public sealed partial class ComputeShaderCompiler : IComputeShaderCompiler
{
	/// <summary>
	/// Constant-buffer registers a game kernel may declare. The D3D12 compute root signature binds only these; b12
	/// is the engine's WolfView and b27 the bindless counts. Any other register fails pipeline creation with a bare
	/// E_INVALIDARG at first use, so the importer rejects it with a message instead.
	/// </summary>
	private static readonly uint[] AllowedConstantBufferRegisters = [0, 1, 2, 11, 12, 27];

	private readonly string _publicModuleDirectory;

	public ComputeShaderCompiler(EngineShaderOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		_publicModuleDirectory = Path.Combine(Path.GetFullPath(options.EngineContentRoot), "Shaders", "Public");
		if (Directory.Exists(_publicModuleDirectory) == false)
			throw new DirectoryNotFoundException($"Public shader module directory '{_publicModuleDirectory}' does not exist.");
	}

	public IReadOnlyList<CompiledShaderArtifact> Compile(string sourcePath, ShaderProgramId programId, GraphicsBackendKind backendKind)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
		var entryPoints = FindEntryPoints(File.ReadAllText(sourcePath));
		if (entryPoints.Count == 0)
		{
			throw new InvalidDataException(
				$"'{Path.GetFileName(sourcePath)}' has no kernels. Mark each one with [shader(\"compute\")].");
		}

		var kernels = new List<CompiledShaderArtifact>(entryPoints.Count);
		foreach (var entryPoint in entryPoints)
		{
			CompiledComputeShaderWithReflection compiled;
			try
			{
				compiled = ShaderCompiler.CompileComputeShader(sourcePath, entryPoint, backendKind, [_publicModuleDirectory]);
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				throw new InvalidDataException(
					$"'{Path.GetFileName(sourcePath)}' kernel '{entryPoint}' failed to compile: {exception.Message}", exception);
			}

			if (backendKind == GraphicsBackendKind.D3D12)
			{
				ValidateConstantBufferRegisters(sourcePath, entryPoint, compiled.ReflectionLayout);
			}

			// The bytecode identifies the kernel's content, so pipelines keyed by it rebuild exactly when it changes.
			var contentKey = Convert.ToHexString(SHA256.HashData(compiled.Bytecode.Span))[..32];
			kernels.Add(new CompiledShaderArtifact(
				ShaderRequest.Compute(programId, entryPoint, backendKind),
				contentKey,
				new ShaderBytecodeSet(compute: compiled.Bytecode, computeThreadGroupSize: compiled.ThreadGroupSize),
				compiled.ReflectionLayout,
				compiled.ThreadGroupSize));
		}

		return kernels;
	}

	/// <summary>The functions marked <c>[shader("compute")]</c>, in source order. Commented-out code is skipped.</summary>
	public static IReadOnlyList<string> FindEntryPoints(string source)
	{
		var withoutComments = CommentPattern().Replace(source, " ");
		var entryPoints = new List<string>();
		foreach (Match match in EntryPointPattern().Matches(withoutComments))
		{
			var name = match.Groups[1].Value;
			if (entryPoints.Contains(name, StringComparer.Ordinal) == false)
			{
				entryPoints.Add(name);
			}
		}

		return entryPoints;
	}

	private static void ValidateConstantBufferRegisters(string sourcePath, string entryPoint, ShaderReflectionLayout reflection)
	{
		foreach (var buffer in reflection.ConstantBuffersByName.Values)
		{
			if (Array.IndexOf(AllowedConstantBufferRegisters, buffer.RegisterIndex) < 0)
			{
				throw new InvalidDataException(
					$"'{Path.GetFileName(sourcePath)}' kernel '{entryPoint}' declares cbuffer '{buffer.Name}' at b{buffer.RegisterIndex}. " +
					"Game kernels can use b0, b1, b2 and b11.");
			}
		}
	}

	[GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
	private static partial Regex CommentPattern();

	// [shader("compute")], then any further attributes such as [numthreads(8, 8, 1)], then the function.
	[GeneratedRegex(@"\[\s*shader\s*\(\s*""compute""\s*\)\s*\](?:\s*\[[^\]]*\])*\s*void\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(")]
	private static partial Regex EntryPointPattern();
}
