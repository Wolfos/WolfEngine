#nullable enable

using System;

namespace WolfEngine.Rendering;

public sealed class ShaderResourceBindingLayout
{
	public ShaderResourceBindingLayout(string name, uint registerIndex)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			throw new ArgumentException("Shader resource name cannot be null or empty.", nameof(name));
		}

		Name = name;
		RegisterIndex = registerIndex;
	}

	public string Name { get; }

	public uint RegisterIndex { get; }
}
