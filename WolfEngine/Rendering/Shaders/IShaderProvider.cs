#nullable enable

using System;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Shaders;

public interface IShaderProvider
{
	long Revision { get; }
	event Action<long>? RevisionChanged;
	CompiledShaderArtifact GetArtifact(ShaderRequest request);
	void SetProjectRoot(string? projectRootPath);
	ShaderReloadResult Reload(GraphicsBackendKind backendKind);
}

public static class ShaderProviderExtensions
{
	public static CompiledComputeShaderWithReflection GetComputeShaderWithReflection(this IShaderProvider provider,
		ShaderProgramId programId, string entryPoint, GraphicsBackendKind backendKind, params string[] defines)
	{
		var artifact = provider.GetArtifact(ShaderRequest.Compute(programId, entryPoint, backendKind, defines));
		return new CompiledComputeShaderWithReflection(artifact.Bytecode.Compute!.Value, artifact.ReflectionLayout,
			artifact.ThreadGroupSize!.Value);
	}

	public static CompiledGraphicsShaderWithReflection GetGraphicsShaderWithReflection(this IShaderProvider provider,
		ShaderProgramId programId, string vertexEntryPoint, string pixelEntryPoint, GraphicsBackendKind backendKind,
		params string[] defines)
	{
		var artifact = provider.GetArtifact(ShaderRequest.Graphics(programId, vertexEntryPoint, pixelEntryPoint, backendKind, defines));
		return new CompiledGraphicsShaderWithReflection(artifact.Bytecode, artifact.ReflectionLayout);
	}
}
