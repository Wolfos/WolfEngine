using WolfEngine.Importing;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Projects;

internal static class TextureMipGenerator
{
	public static TextureMipData[] GenerateRgba32MipChain(TextureMipData baseMip)
	{
		return GenerateMipChain(baseMip, TextureFormat.Rgba8Unorm);
	}

	/// <summary>
	/// Builds the full mip chain down to 1x1 with a 2x2 box filter. Each of the base mip's slices (a cube's
	/// faces) is filtered on its own, so faces never blend into each other and every mip keeps the same slice count.
	/// </summary>
	public static TextureMipData[] GenerateMipChain(TextureMipData baseMip, TextureFormat format)
	{
		ArgumentNullException.ThrowIfNull(baseMip.Data);
		if (format != TextureFormat.Rgba8Unorm && format != TextureFormat.Rgba16Float)
		{
			throw new NotSupportedException($"Mip generation does not support {format}.");
		}

		var mips = new List<TextureMipData> { baseMip };
		var current = baseMip;
		while (current.Width > 1 || current.Height > 1)
		{
			current = format == TextureFormat.Rgba16Float
				? DownsampleRgba16Float(current)
				: DownsampleRgba32(current);
			mips.Add(current);
		}

		return [.. mips];
	}

	private static TextureMipData DownsampleRgba32(TextureMipData source)
	{
		var targetWidth = Math.Max(1, source.Width / 2);
		var targetHeight = Math.Max(1, source.Height / 2);
		var sourceSliceLength = source.Width * source.Height * 4;
		var targetSliceLength = targetWidth * targetHeight * 4;
		var result = new byte[targetSliceLength * source.Depth];

		for (var slice = 0; slice < source.Depth; slice++)
		{
			var sourceBase = slice * sourceSliceLength;
			var targetBase = slice * targetSliceLength;
			for (var y = 0; y < targetHeight; y++)
			{
				for (var x = 0; x < targetWidth; x++)
				{
					var sumR = 0;
					var sumG = 0;
					var sumB = 0;
					var sumA = 0;
					var sampleCount = 0;
					var srcStartX = x * 2;
					var srcStartY = y * 2;

					for (var sampleY = 0; sampleY < 2; sampleY++)
					{
						var srcY = Math.Min(source.Height - 1, srcStartY + sampleY);
						for (var sampleX = 0; sampleX < 2; sampleX++)
						{
							var srcX = Math.Min(source.Width - 1, srcStartX + sampleX);
							var srcIndex = sourceBase + (srcY * source.Width + srcX) * 4;
							sumR += source.Data[srcIndex + 0];
							sumG += source.Data[srcIndex + 1];
							sumB += source.Data[srcIndex + 2];
							sumA += source.Data[srcIndex + 3];
							sampleCount++;
						}
					}

					var destIndex = targetBase + (y * targetWidth + x) * 4;
					result[destIndex + 0] = (byte)(sumR / sampleCount);
					result[destIndex + 1] = (byte)(sumG / sampleCount);
					result[destIndex + 2] = (byte)(sumB / sampleCount);
					result[destIndex + 3] = (byte)(sumA / sampleCount);
				}
			}
		}

		return new TextureMipData(targetWidth, targetHeight, result, source.Depth);
	}

	private static TextureMipData DownsampleRgba16Float(TextureMipData source)
	{
		var targetWidth = Math.Max(1, source.Width / 2);
		var targetHeight = Math.Max(1, source.Height / 2);
		var sourceValues = HdrPixels.UnpackHalfRgba(source.Data);
		var sourceSliceLength = source.Width * source.Height * 4;
		var targetSliceLength = targetWidth * targetHeight * 4;
		var result = new float[targetSliceLength * source.Depth];

		for (var slice = 0; slice < source.Depth; slice++)
		{
			var sourceBase = slice * sourceSliceLength;
			var targetBase = slice * targetSliceLength;
			for (var y = 0; y < targetHeight; y++)
			{
				for (var x = 0; x < targetWidth; x++)
				{
					var destIndex = targetBase + (y * targetWidth + x) * 4;
					for (var channel = 0; channel < 4; channel++)
					{
						var sum = 0.0f;
						var sampleCount = 0;
						for (var sampleY = 0; sampleY < 2; sampleY++)
						{
							var srcY = Math.Min(source.Height - 1, y * 2 + sampleY);
							for (var sampleX = 0; sampleX < 2; sampleX++)
							{
								var srcX = Math.Min(source.Width - 1, x * 2 + sampleX);
								sum += sourceValues[sourceBase + (srcY * source.Width + srcX) * 4 + channel];
								sampleCount++;
							}
						}

						result[destIndex + channel] = sum / sampleCount;
					}
				}
			}
		}

		return new TextureMipData(targetWidth, targetHeight, HdrPixels.PackHalfRgba(result), source.Depth);
	}
}
