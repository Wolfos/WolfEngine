#nullable enable

using System.Text;
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
			var source = shaderCompiler.GetMetalSource(shaderPath, vertexEntryPoint, pixelEntryPoint, defines);
			var bytes = Encoding.UTF8.GetBytes(source);
			return new ShaderBytecodeSet(bytes, bytes);
		}

		var vertex = shaderCompiler.GetDxil(shaderPath, vertexEntryPoint, "vs_6_0", defines);
		var pixel = shaderCompiler.GetDxil(shaderPath, pixelEntryPoint, "ps_6_0", defines);
		return new ShaderBytecodeSet(vertex, pixel);
	}
}
