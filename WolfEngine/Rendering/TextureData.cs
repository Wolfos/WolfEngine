namespace WolfEngine.Rendering;

/// <summary>
/// One mip level of texture content. For volume and cube textures, <see cref="Data"/> holds
/// <see cref="Depth"/> slices back to back, each laid out as a 2D mip of the same size. A cube's
/// slices are its faces in <see cref="TextureDimension.Cube"/> order.
/// </summary>
public readonly record struct TextureMipData(
	int Width,
	int Height,
	byte[] Data,
	int Depth = 1);

public enum TextureCompressionFamily
{
	None = 0,
	Bc,
	Astc
}

public static class TextureFormatUtilities
{
	public static bool IsCompressed(TextureFormat format) => format is
		TextureFormat.Bc1Unorm or
		TextureFormat.Bc3Unorm or
		TextureFormat.Bc4Unorm or
		TextureFormat.Bc5Unorm or
		TextureFormat.Bc7Unorm or
		TextureFormat.Bc6hUfloat or
		TextureFormat.Astc4x4Unorm;

	/// <summary>
	/// Whether a content texture in this format may be created with unordered-access support, so a compute
	/// shader can write to it. Both backends must agree: creating the resource without it and then binding
	/// a UAV over it is invalid, which D3D12 reports as a command-list recording failure and Metal does not
	/// report at all.
	/// </summary>
	/// <remarks>
	/// sRGB is excluded because typed unordered access is not defined for sRGB formats. Rgba16Float is the
	/// terrain height preview format and is written (never typed-loaded) by the authoring brushes.
	/// </remarks>
	public static bool SupportsUnorderedAccess(TextureFormat format, bool isSrgb) =>
		isSrgb == false &&
		format is TextureFormat.Rgba8Unorm or TextureFormat.Bgra8Unorm or TextureFormat.Rgba16Float;

	public static int GetBlockWidth(TextureFormat format) => format switch
	{
		TextureFormat.Bc1Unorm => 4,
		TextureFormat.Bc3Unorm => 4,
		TextureFormat.Bc4Unorm => 4,
		TextureFormat.Bc5Unorm => 4,
		TextureFormat.Bc7Unorm => 4,
		TextureFormat.Bc6hUfloat => 4,
		TextureFormat.Astc4x4Unorm => 4,
		_ => 1
	};

	public static int GetBlockHeight(TextureFormat format) => format switch
	{
		TextureFormat.Bc1Unorm => 4,
		TextureFormat.Bc3Unorm => 4,
		TextureFormat.Bc4Unorm => 4,
		TextureFormat.Bc5Unorm => 4,
		TextureFormat.Bc7Unorm => 4,
		TextureFormat.Bc6hUfloat => 4,
		TextureFormat.Astc4x4Unorm => 4,
		_ => 1
	};

	public static int GetBytesPerBlock(TextureFormat format) => format switch
	{
		TextureFormat.Bc1Unorm => 8,
		TextureFormat.Bc3Unorm => 16,
		TextureFormat.Bc4Unorm => 8,
		TextureFormat.Bc5Unorm => 16,
		TextureFormat.Bc7Unorm => 16,
		TextureFormat.Bc6hUfloat => 16,
		TextureFormat.Astc4x4Unorm => 16,
		TextureFormat.Bgra8Unorm => 4,
		TextureFormat.Rgba8Unorm => 4,
		TextureFormat.Rgba8Uint => 4,
		TextureFormat.R16Unorm => 2,
		TextureFormat.Rg16Float => 4,
		TextureFormat.Rgba16Float => 8,
		TextureFormat.R32Float => 4,
		TextureFormat.Rg32Float => 8,
		TextureFormat.R32Uint => 4,
		TextureFormat.D32Float => 4,
		_ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported texture format.")
	};

	public static int GetBytesPerRow(TextureFormat format, int width)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);

		if (IsCompressed(format))
		{
			var blocksX = (width + GetBlockWidth(format) - 1) / GetBlockWidth(format);
			return blocksX * GetBytesPerBlock(format);
		}

		return width * GetBytesPerBlock(format);
	}

	/// <summary>
	/// Number of slices a mip of a texture holds back to back: the mip's depth for 3D textures, which halves
	/// each level, and one per face for cube textures, which keeps all six at every level.
	/// </summary>
	public static int GetMipSliceCount(TextureDimension dimension, int depth, int mipLevel)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
		ArgumentOutOfRangeException.ThrowIfNegative(mipLevel);

		return dimension switch
		{
			TextureDimension.Texture3D => Math.Max(depth >> mipLevel, 1),
			TextureDimension.Cube => TextureDescriptor.CubeFaceCount,
			_ => 1
		};
	}

	/// <summary>
	/// Number of array layers the GPU resource has, each with its own subresource per mip: six for a cube,
	/// one otherwise. A 3D texture's slices live inside one subresource and do not count.
	/// </summary>
	public static int GetArrayLayerCount(TextureDimension dimension)
	{
		return dimension == TextureDimension.Cube
			? TextureDescriptor.CubeFaceCount
			: 1;
	}

	public static int GetMipDataSize(TextureFormat format, int width, int height, int depth = 1)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);

		if (IsCompressed(format))
		{
			var blocksX = (width + GetBlockWidth(format) - 1) / GetBlockWidth(format);
			var blocksY = (height + GetBlockHeight(format) - 1) / GetBlockHeight(format);
			return blocksX * blocksY * depth * GetBytesPerBlock(format);
		}

		return width * height * depth * GetBytesPerBlock(format);
	}
}
