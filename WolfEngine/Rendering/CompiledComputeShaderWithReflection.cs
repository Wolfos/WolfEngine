#nullable enable

namespace WolfEngine.Rendering;

public readonly struct CompiledComputeShaderWithReflection
{
	public CompiledComputeShaderWithReflection(ReadOnlyMemory<byte> bytecode, ShaderReflectionLayout reflectionLayout)
	{
		if (bytecode.IsEmpty)
		{
			throw new ArgumentException("Compiled compute bytecode cannot be empty.", nameof(bytecode));
		}

		ArgumentNullException.ThrowIfNull(reflectionLayout);

		Bytecode = bytecode;
		ReflectionLayout = reflectionLayout;
	}

	public ReadOnlyMemory<byte> Bytecode { get; }

	public ShaderReflectionLayout ReflectionLayout { get; }
}
