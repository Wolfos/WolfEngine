using WolfEngine.AssetPipeline;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering;

/// <summary>
/// A game's compute shader: a <c>.compute.slang</c> file in the project, compiled at import for the backend the
/// editor or build runs on. Each function marked <c>[shader("compute")]</c> is one kernel. A reimport resolves to
/// a new instance, so code that caches pipelines per instance picks up the change.
/// </summary>
[RuntimeAsset(AssetType.ComputeShader, typeof(ComputeShaderAssetSummary), typeof(IComputeShaderRuntimeResolver))]
public sealed class ComputeShader
{
	private readonly Dictionary<string, CompiledShaderArtifact> _kernels = new(StringComparer.Ordinal);

	public ComputeShader(string name, IReadOnlyList<CompiledShaderArtifact> kernels)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentNullException.ThrowIfNull(kernels);
		if (kernels.Count == 0)
		{
			throw new ArgumentException($"Compute shader '{name}' has no kernels.", nameof(kernels));
		}

		foreach (var kernel in kernels)
		{
			if (kernel.Request.Kind != ShaderRequestKind.Compute || kernel.ThreadGroupSize is null)
			{
				throw new ArgumentException($"Compute shader '{name}' contains a non-compute artifact.", nameof(kernels));
			}

			if (kernel.Request.BackendKind != kernels[0].Request.BackendKind)
			{
				throw new ArgumentException($"Compute shader '{name}' mixes kernels for different backends.", nameof(kernels));
			}

			if (_kernels.TryAdd(kernel.Request.ComputeEntryPoint!, kernel) == false)
			{
				throw new ArgumentException(
					$"Compute shader '{name}' declares entry point '{kernel.Request.ComputeEntryPoint}' twice.", nameof(kernels));
			}
		}

		Name = name;
		BackendKind = kernels[0].Request.BackendKind;
	}

	public string Name { get; }

	public GraphicsBackendKind BackendKind { get; }

	public IReadOnlyCollection<string> EntryPoints => _kernels.Keys;

	public CompiledShaderArtifact GetKernel(string entryPoint)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);
		return _kernels.TryGetValue(entryPoint, out var kernel)
			? kernel
			: throw new KeyNotFoundException(
				$"Compute shader '{Name}' has no entry point '{entryPoint}'. It declares: {string.Join(", ", _kernels.Keys)}.");
	}
}
