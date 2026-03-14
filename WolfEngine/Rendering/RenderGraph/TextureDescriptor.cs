namespace WolfEngine.Rendering;

/// <summary>
/// Placeholder description for textures requested through the render graph.
/// Responsible for communicating usage requirements until there is a concrete GPU abstraction.
/// </summary>
public readonly struct TextureDescriptor
{
	public TextureDescriptor(int width, int height, TextureFormat format, TextureUsage usage, ColorRGBA? clearColor = null,
		float depthClear = 1.0f)
	{
		Width = width;
		Height = height;
		Format = format;
		Usage = usage;
		ClearColor = clearColor ?? default;
		DepthClear = depthClear;
	}

	public int Width { get; }

	public int Height { get; }

	public TextureFormat Format { get; }

	public TextureUsage Usage { get; }

	public ColorRGBA ClearColor { get; }

	public float DepthClear { get; }
}

public enum TextureFormat
{
	Unknown = 0,
	Bgra8Unorm,
	Rgba8Unorm,
	Rg16Float,
	Rgba16Float,
	R32Float,
	D32Float
}

[Flags]
public enum TextureUsage
{
	None = 0,
	RenderTarget = 1 << 0,
	DepthStencil = 1 << 1,
	ShaderResource = 1 << 2,
	UnorderedAccess = 1 << 3
}
