namespace WolfEngine.Rendering;

/// <summary>
/// Placeholder description for textures requested through the render graph.
/// Responsible for communicating usage requirements until there is a concrete GPU abstraction.
/// </summary>
public readonly struct TextureDescriptor
{
	public TextureDescriptor(int width, int height, TextureFormat format, TextureUsage usage, ColorRGBA? clearColor = null,
		float depthClear = 1.0f, int mipLevels = 1, bool isSrgb = false,
		TextureDimension dimension = TextureDimension.Texture2D, int depth = 1)
	{
		if (dimension == TextureDimension.Texture2D && depth != 1)
		{
			throw new ArgumentOutOfRangeException(nameof(depth), "2D textures must have a depth of one.");
		}

		if (dimension == TextureDimension.Texture3D)
		{
			ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
			// Volume textures are sampled content only. Neither backend binds a 3D render target, depth
			// target or UAV today, so reject them here rather than at a backend-specific failure point.
			if ((usage & (TextureUsage.RenderTarget | TextureUsage.DepthStencil | TextureUsage.UnorderedAccess)) != 0)
			{
				throw new ArgumentException("3D textures support shader-resource usage only.", nameof(usage));
			}
		}

		Width = width;
		Height = height;
		Format = format;
		Usage = usage;
		ClearColor = clearColor ?? default;
		DepthClear = depthClear;
		MipLevels = mipLevels;
		IsSrgb = isSrgb;
		Dimension = dimension;
		Depth = depth;
	}

	public int Width { get; }

	public int Height { get; }

	/// <summary>Number of slices along Z. Always one for 2D textures.</summary>
	public int Depth { get; }

	public TextureDimension Dimension { get; }

	public TextureFormat Format { get; }

	public TextureUsage Usage { get; }

	public ColorRGBA ClearColor { get; }

	public float DepthClear { get; }

	public int MipLevels { get; }

	public bool IsSrgb { get; }
}

public enum TextureDimension
{
	Texture2D = 0,
	Texture3D = 1
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
