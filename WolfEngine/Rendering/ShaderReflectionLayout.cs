#nullable enable

namespace WolfEngine.Rendering;

public sealed class ShaderReflectionLayout
{
	private readonly Dictionary<string, ShaderConstantBufferLayout> _buffersByName;
	private readonly Dictionary<uint, ShaderConstantBufferLayout> _buffersByRegister;

	public ShaderReflectionLayout(IEnumerable<ShaderConstantBufferLayout> constantBuffers)
	{
		ArgumentNullException.ThrowIfNull(constantBuffers);

		_buffersByName = new(StringComparer.Ordinal);
		_buffersByRegister = new();

		foreach (var buffer in constantBuffers)
		{
			if (_buffersByName.TryAdd(buffer.Name, buffer) == false)
			{
				throw new InvalidOperationException($"Duplicate reflected constant buffer name '{buffer.Name}'.");
			}

			if (_buffersByRegister.TryAdd(buffer.RegisterIndex, buffer) == false)
			{
				throw new InvalidOperationException(
					$"Duplicate reflected constant buffer register 'b{buffer.RegisterIndex}'.");
			}
		}
	}

	public IReadOnlyDictionary<string, ShaderConstantBufferLayout> ConstantBuffersByName => _buffersByName;

	public bool TryGetConstantBuffer(string name, out ShaderConstantBufferLayout layout)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			layout = null!;
			return false;
		}

		return _buffersByName.TryGetValue(name, out layout!);
	}

	public ShaderConstantBufferLayout GetConstantBuffer(string name)
	{
		if (TryGetConstantBuffer(name, out var layout))
		{
			return layout;
		}

		throw new InvalidOperationException($"Reflected constant buffer '{name}' was not found.");
	}

	public bool TryGetConstantBuffer(uint registerIndex, out ShaderConstantBufferLayout layout)
	{
		return _buffersByRegister.TryGetValue(registerIndex, out layout!);
	}

	public ShaderConstantBufferLayout GetConstantBufferByRegister(uint registerIndex)
	{
		if (TryGetConstantBuffer(registerIndex, out var layout))
		{
			return layout;
		}

		throw new InvalidOperationException($"Reflected constant buffer register 'b{registerIndex}' was not found.");
	}
}
