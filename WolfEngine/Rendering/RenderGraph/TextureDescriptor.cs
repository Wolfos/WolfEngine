namespace WolfEngine.Rendering;

/// <summary>
/// Placeholder description for textures requested through the render graph.
/// Responsible for communicating usage requirements until there is a concrete GPU abstraction.
/// </summary>
public readonly struct TextureDescriptor
{
	public TextureDescriptor(int width, int height, TextureFormat format)
	{
		Width = width;
		Height = height;
		Format = format;
	}

	public int Width { get; }

	public int Height { get; }

	public TextureFormat Format { get; }
}

public enum TextureFormat
{
	Unknown = 0,
	Rgba8Unorm,
	Rgba16Float,
	D32Float
}
