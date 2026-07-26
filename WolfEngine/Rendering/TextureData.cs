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
		TextureFormat.Bc1Unorm or
		TextureFormat.Bc3Unorm or
		TextureFormat.Bc4Unorm or
		TextureFormat.Bc5Unorm or
		TextureFormat.Bc7Unorm or
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
		TextureFormat.Astc4x4Unorm => 16,
		TextureFormat.Bgra8Unorm => 4,
		TextureFormat.Rgba8Unorm => 4,
		TextureFormat.Rgba8Uint => 4,
		TextureFormat.R16Unorm => 2,
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
