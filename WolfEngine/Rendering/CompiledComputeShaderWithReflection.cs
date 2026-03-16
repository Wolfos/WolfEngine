#nullable enable

namespace WolfEngine.Rendering;

public readonly struct CompiledComputeShaderWithReflection
{
	public CompiledComputeShaderWithReflection(
		ReadOnlyMemory<byte> bytecode,
		ShaderReflectionLayout reflectionLayout,
		ComputeThreadGroupSize threadGroupSize)
	{
		if (bytecode.IsEmpty)
		{
			throw new ArgumentException("Compiled compute bytecode cannot be empty.", nameof(bytecode));
		}

		ArgumentNullException.ThrowIfNull(reflectionLayout);

		Bytecode = bytecode;
		ReflectionLayout = reflectionLayout;
		ThreadGroupSize = threadGroupSize;
	}

	public ReadOnlyMemory<byte> Bytecode { get; }

	public ShaderReflectionLayout ReflectionLayout { get; }

	public ComputeThreadGroupSize ThreadGroupSize { get; }
}
