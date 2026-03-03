#nullable enable

using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

public readonly struct CompiledGraphicsShaderWithReflection
{
	public CompiledGraphicsShaderWithReflection(ShaderBytecodeSet bytecode, ShaderReflectionLayout reflectionLayout)
	{
		ArgumentNullException.ThrowIfNull(reflectionLayout);

		if (bytecode.Vertex is not { } vertex || vertex.IsEmpty ||
		    bytecode.Pixel is not { } pixel || pixel.IsEmpty)
		{
			throw new ArgumentException("Compiled graphics bytecode must include both vertex and pixel stages.", nameof(bytecode));
		}

		Bytecode = bytecode;
		ReflectionLayout = reflectionLayout;
	}

	public ShaderBytecodeSet Bytecode { get; }

	public ShaderReflectionLayout ReflectionLayout { get; }
}
