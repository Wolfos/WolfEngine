using System;

namespace WolfEngine.Rendering;

public readonly record struct TextureMipData(
	int Width,
	int Height,
	byte[] Data);

public enum TextureCompressionFamily
{
	None = 0,
	Bc,
	Astc
}

public static class TextureFormatUtilities
{
	public static bool IsCompressed(TextureFormat format) => format is
		TextureFormat.Bc5Unorm or
		TextureFormat.Bc7Unorm or
		TextureFormat.Astc4x4Unorm;

	public static int GetBlockWidth(TextureFormat format) => format switch
	{
		TextureFormat.Bc5Unorm => 4,
		TextureFormat.Bc7Unorm => 4,
		TextureFormat.Astc4x4Unorm => 4,
		_ => 1
	};

	public static int GetBlockHeight(TextureFormat format) => format switch
	{
		TextureFormat.Bc5Unorm => 4,
		TextureFormat.Bc7Unorm => 4,
		TextureFormat.Astc4x4Unorm => 4,
		_ => 1
	};

	public static int GetBytesPerBlock(TextureFormat format) => format switch
	{
		TextureFormat.Bc5Unorm => 16,
		TextureFormat.Bc7Unorm => 16,
		TextureFormat.Astc4x4Unorm => 16,
		TextureFormat.Bgra8Unorm => 4,
		TextureFormat.Rgba8Unorm => 4,
		TextureFormat.Rg16Float => 4,
		TextureFormat.Rgba16Float => 8,
		TextureFormat.R32Float => 4,
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

	public static int GetMipDataSize(TextureFormat format, int width, int height)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

		if (IsCompressed(format))
		{
			var blocksX = (width + GetBlockWidth(format) - 1) / GetBlockWidth(format);
			var blocksY = (height + GetBlockHeight(format) - 1) / GetBlockHeight(format);
			return blocksX * blocksY * GetBytesPerBlock(format);
		}

		return width * height * GetBytesPerBlock(format);
	}
}
