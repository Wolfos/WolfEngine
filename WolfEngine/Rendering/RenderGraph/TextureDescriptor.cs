namespace WolfEngine.Rendering;

/// <summary>
/// Placeholder description for textures requested through the render graph.
/// Responsible for communicating usage requirements until there is a concrete GPU abstraction.
/// </summary>
public readonly struct TextureDescriptor
{
	public TextureDescriptor(int width, int height, TextureFormat format, TextureUsage usage)
	{
		Width = width;
		Height = height;
		Format = format;
		Usage = usage;
	}

	public int Width { get; }

	public int Height { get; }

	public TextureFormat Format { get; }

	public TextureUsage Usage { get; }
}

public enum TextureFormat
{
	Unknown = 0,
	Bgra8Unorm,
	Rgba8Unorm,
	Rgba16Float,
	D32Float
}

[Flags]
public enum TextureUsage
{
	None = 0,
	RenderTarget = 1 << 0,
	DepthStencil = 1 << 1,
	ShaderResource = 1 << 2
}
