#nullable enable

using System;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Shaders;

/// <summary>
/// Temporary compatibility boundary while renderer constructors move from IShaderCompiler to IShaderProvider.
/// Source paths are resolved through the engine catalog and never against the process output directory.
/// </summary>
internal sealed class LegacyShaderCompilerAdapter : IShaderCompiler
{
	private readonly IShaderProvider _provider;
	private readonly EngineShaderCatalog _catalog;

	public LegacyShaderCompilerAdapter(IShaderProvider provider, EngineShaderCatalog catalog)
	{
		_provider = provider;
		_catalog = catalog;
	}

	public long Revision => _provider.Revision;
	public event Action<long>? RevisionChanged
	{
		add => _provider.RevisionChanged += value;
		remove => _provider.RevisionChanged -= value;
	}
	public CompiledShaderArtifact GetArtifact(ShaderRequest request) => _provider.GetArtifact(request);
	public void SetProjectRoot(string? projectRootPath) => _provider.SetProjectRoot(projectRootPath);
	public ShaderReloadResult Reload(GraphicsBackendKind backendKind) => _provider.Reload(backendKind);
}
