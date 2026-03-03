#nullable enable

using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

internal static class GraphicsShaderCompiler
{
	public static ShaderBytecodeSet Compile(
		IShaderCompiler shaderCompiler,
		GraphicsBackendKind backendKind,
		string shaderPath,
		string vertexEntryPoint,
		string pixelEntryPoint,
		params string[] defines)
	{
		ArgumentNullException.ThrowIfNull(shaderCompiler);

		if (backendKind == GraphicsBackendKind.Metal)
		{
			var library = shaderCompiler.GetMetalLibrary(shaderPath, vertexEntryPoint, pixelEntryPoint, defines);
			return new ShaderBytecodeSet(library, library);
		}

		var vertex = shaderCompiler.GetDxil(shaderPath, vertexEntryPoint, "vs_6_0", defines);
		var pixel = shaderCompiler.GetDxil(shaderPath, pixelEntryPoint, "ps_6_0", defines);
		return new ShaderBytecodeSet(vertex, pixel);
	}

	public static CompiledGraphicsShaderWithReflection CompileWithReflection(
		IShaderCompiler shaderCompiler,
		GraphicsBackendKind backendKind,
		string shaderPath,
		string vertexEntryPoint,
		string pixelEntryPoint,
		params string[] defines)
	{
		ArgumentNullException.ThrowIfNull(shaderCompiler);

		return shaderCompiler.GetGraphicsShaderWithReflection(
			shaderPath,
			vertexEntryPoint,
			pixelEntryPoint,
			backendKind,
			defines);
	}
}
