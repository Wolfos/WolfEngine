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
			// Neither backend binds a 3D render target or depth target. Volume UAVs are supported for
			// compute workloads such as camera-aligned froxel grids.
			if ((usage & (TextureUsage.RenderTarget | TextureUsage.DepthStencil)) != 0)
			{
				throw new ArgumentException("3D textures cannot be render targets or depth targets.", nameof(usage));
			}
		}

		if (dimension == TextureDimension.Cube)
		{
			if (depth != CubeFaceCount)
			{
				throw new ArgumentOutOfRangeException(nameof(depth), $"Cube textures must have a depth of {CubeFaceCount}, one slice per face.");
			}

			if (width != height)
			{
				throw new ArgumentException("Cube texture faces must be square.", nameof(height));
			}

			// Cubes are sampled through GetTextureCube only. Neither backend has a bindless view that renders
			// to or writes a cube face yet.
			if ((usage & (TextureUsage.RenderTarget | TextureUsage.DepthStencil | TextureUsage.UnorderedAccess)) != 0)
			{
				throw new ArgumentException("Cube textures can only be shader resources.", nameof(usage));
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

	/// <summary>Number of faces in a cube texture, which is also its <see cref="Depth"/>.</summary>
	public const int CubeFaceCount = 6;

	public int Width { get; }

	public int Height { get; }

	/// <summary>
	/// Number of slices: along Z for 3D textures, one per face for cube textures
	/// (<see cref="CubeFaceCount"/>), and always one for 2D textures.
	/// </summary>
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
	Texture3D = 1,

	/// <summary>
	/// Six square faces in the order +X, -X, +Y, -Y, +Z, -Z, which is the face order of both D3D12 and Metal.
	/// Each face keeps its full size in every mip, so a mip holds six slices back to back.
	/// </summary>
	Cube = 2
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
	R32Uint = 15,
	Rg32Float = 16,
	/// <summary>Unsigned half-float HDR RGB, 4x4 blocks of 16 bytes. Negative values cannot be represented.</summary>
	Bc6hUfloat = 17
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
