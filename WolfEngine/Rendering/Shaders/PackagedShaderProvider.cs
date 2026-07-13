using WolfEngine.AssetPipeline;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Shaders;

public interface IPackagedShaderProvider : IShaderProvider
{
}

public sealed class PackagedShaderProvider : IPackagedShaderProvider
{
	private readonly Dictionary<ShaderRequest, CompiledShaderArtifact> _artifacts = [];

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
			return artifact;

		Console.Error.WriteLine($"missing cooked shader request: {request}");
		throw new KeyNotFoundException($"Shader request '{request}' is not present in the cooked packs.");
	}

	public void SetProjectRoot(string? projectRootPath)
	{
	}

	public ShaderReloadResult Reload(GraphicsBackendKind backendKind) => new(0, []);
}
