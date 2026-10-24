using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Importing;

public static class ComputeShaderImport
{
	/// <summary>
	/// Compute shader assets are <c>.compute.slang</c> files. Other <c>.slang</c> files are modules they import,
	/// not assets.
	/// </summary>
	public const string SourceExtension = ".compute.slang";

	public static string GetAssetName(string sourcePath)
	{
		var fileName = Path.GetFileName(sourcePath);
		return fileName.EndsWith(SourceExtension, StringComparison.OrdinalIgnoreCase)
			? fileName[..^SourceExtension.Length]
			: Path.GetFileNameWithoutExtension(fileName);
	}

	/// <summary>The backend whose kernels a runtime artifact target holds.</summary>
	public static GraphicsBackendKind GetBackendKind(string artifactTarget) => artifactTarget switch
	{
		"d3d12" => GraphicsBackendKind.D3D12,
		"metal" => GraphicsBackendKind.Metal,
		_ => throw new NotSupportedException($"Compute shaders cannot be compiled for artifact target '{artifactTarget}'.")
	};
}

/// <summary>Compiles a game's compute shader source. Editor tooling implements it; a built game only loads the result.</summary>
public interface IComputeShaderCompiler
{
	/// <summary>
	/// Compiles every function marked <c>[shader("compute")]</c> in <paramref name="sourcePath"/> for one backend.
	/// Throws <see cref="InvalidDataException"/> with the compiler's message when the source does not compile.
	/// </summary>
	IReadOnlyList<CompiledShaderArtifact> Compile(string sourcePath, ShaderProgramId programId, GraphicsBackendKind backendKind);
}
