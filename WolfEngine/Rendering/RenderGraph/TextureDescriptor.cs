namespace WolfEngine.Rendering;

/// <summary>
/// Placeholder description for textures requested through the render graph.
/// Responsible for communicating usage requirements until there is a concrete GPU abstraction.
/// </summary>
public readonly struct TextureDescriptor
{
	public TextureDescriptor(int width, int height, TextureFormat format, TextureUsage usage, ColorRGBA? clearColor = null,
		float depthClear = 1.0f, int mipLevels = 1, bool isSrgb = false)
	{
		Width = width;
		Height = height;
		Format = format;
		Usage = usage;
		ClearColor = clearColor ?? default;
		DepthClear = depthClear;
		MipLevels = mipLevels;
		IsSrgb = isSrgb;
	}

	public int Width { get; }

	public int Height { get; }

	public TextureFormat Format { get; }

	public TextureUsage Usage { get; }

	public ColorRGBA ClearColor { get; }

	public float DepthClear { get; }

	public int MipLevels { get; }

	public bool IsSrgb { get; }
}

public enum TextureFormat
{
	Unknown = 0,
	Bgra8Unorm = 1,
	Rgba8Unorm = 2,
	Rgba8Uint = 3,
	R16Unorm = 4,
	Rg16Float = 5,
	Rgba16Float = 6,
	R32Float = 7,
	D32Float = 8,
	Bc3Unorm = 9,
	Bc5Unorm = 10,
	Bc7Unorm = 11,
	Astc4x4Unorm = 12,
	Bc1Unorm = 13,
	Bc4Unorm = 14,
	/// <summary>Single-channel 32-bit unsigned integer. Required for textures written by integer atomics.</summary>
	R32Uint = 15
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
