using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

public sealed class ShaderResourceBindingLayout
{
	public ShaderResourceBindingLayout(string name, uint registerIndex, ShaderStage visibility = ShaderStage.AllGraphics)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			throw new ArgumentException("Shader resource name cannot be null or empty.", nameof(name));
		}

		Name = name;
		RegisterIndex = registerIndex;
		Visibility = visibility;
	}

	public string Name { get; }

	public uint RegisterIndex { get; }

	public ShaderStage Visibility { get; }
}
