using WolfEngine.AssetPipeline;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Shaders;

public interface IPackagedShaderProvider : IShaderProvider
{
}

public sealed class PackagedShaderProvider : IPackagedShaderProvider
{
	private readonly Dictionary<ShaderRequest, CompiledShaderArtifact> _artifacts = [];
	private readonly HashSet<ShaderRequest> _requested = [];

	public PackagedShaderProvider(WolfPackCatalog catalog)
	{
		ArgumentNullException.ThrowIfNull(catalog);
		foreach (var id in catalog.AssetIds)
		{
			if (!string.Equals(catalog.GetEntry(id).Kind, "Shader", StringComparison.Ordinal))
				continue;

			using var stream = new MemoryStream(catalog.Read(id), writable: false);
			var artifact = ShaderArtifactSerializer.Read(stream);
			if (!_artifacts.TryAdd(artifact.Request, artifact))
				throw new InvalidDataException($"Duplicate packaged shader request '{artifact.Request}'.");
		}
	}

	public long Revision => 0;

	public event Action<long>? RevisionChanged
	{
		add { }
		remove { }
	}

	public CompiledShaderArtifact GetArtifact(ShaderRequest request)
	{
		if (_artifacts.TryGetValue(request, out var artifact))
		{
			lock (_requested)
			{
				_requested.Add(request);
			}

			return artifact;
		}

		Console.Error.WriteLine($"missing cooked shader request: {request}");
		throw new KeyNotFoundException($"Shader request '{request}' is not present in the cooked packs.");
	}

	/// <summary>Diagnostic: the bytecode held for every packaged shader, and how much of it has been requested.</summary>
	public string DescribeMemory()
	{
		long totalBytes = 0;
		long requestedBytes = 0;
		int requestedCount;
		lock (_requested)
		{
			requestedCount = _requested.Count;
			foreach (var (request, artifact) in _artifacts)
			{
				var bytes = (long)(artifact.Bytecode.Vertex?.Length ?? 0) +
				            (artifact.Bytecode.Pixel?.Length ?? 0) +
				            (artifact.Bytecode.Compute?.Length ?? 0);
				totalBytes += bytes;
				if (_requested.Contains(request))
				{
					requestedBytes += bytes;
				}
			}
		}

		return $"Shaders: {_artifacts.Count} artifacts hold {totalBytes / (1024.0 * 1024.0):F1} MB of bytecode; " +
		       $"{requestedCount} requested so far, {requestedBytes / (1024.0 * 1024.0):F1} MB of it.";
	}

	public void SetProjectRoot(string? projectRootPath)
	{
	}

	public ShaderReloadResult Reload(GraphicsBackendKind backendKind) => new(0, []);
}
