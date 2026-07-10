#nullable enable

using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

internal static class GraphicsShaderCompiler
{
	public static ShaderBytecodeSet Compile(
		IShaderCompiler shaderCompiler,
		GraphicsBackendKind backendKind,
		ShaderProgramId shaderProgram,
		string vertexEntryPoint,
		string pixelEntryPoint,
		params string[] defines)
	{
		ArgumentNullException.ThrowIfNull(shaderCompiler);

		return shaderCompiler.GetGraphicsShaderWithReflection(shaderProgram, vertexEntryPoint, pixelEntryPoint,
			backendKind, defines).Bytecode;
	}

	public static CompiledGraphicsShaderWithReflection CompileWithReflection(
		IShaderCompiler shaderCompiler,
		GraphicsBackendKind backendKind,
		ShaderProgramId shaderProgram,
		string vertexEntryPoint,
		string pixelEntryPoint,
		params string[] defines)
	{
		ArgumentNullException.ThrowIfNull(shaderCompiler);

		return shaderCompiler.GetGraphicsShaderWithReflection(
			shaderProgram,
			vertexEntryPoint,
			pixelEntryPoint,
			backendKind,
			defines);
	}
}
