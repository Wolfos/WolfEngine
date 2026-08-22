using WolfEngine.Rendering;

namespace WolfEngine.Editor.Projects;

internal static class TextureMipGenerator
{
	public static TextureMipData[] GenerateRgba32MipChain(TextureMipData baseMip)
	{
		ArgumentNullException.ThrowIfNull(baseMip.Data);

		var mips = new List<TextureMipData> { baseMip };
		var current = baseMip;
		while (current.Width > 1 || current.Height > 1)
		{
			current = DownsampleRgba32(current);
			mips.Add(current);
		}

		return [.. mips];
	}

	private static TextureMipData DownsampleRgba32(TextureMipData source)
	{
		var targetWidth = Math.Max(1, source.Width / 2);
		var targetHeight = Math.Max(1, source.Height / 2);
		var result = new byte[targetWidth * targetHeight * 4];

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
						var srcIndex = (srcY * source.Width + srcX) * 4;
						sumR += source.Data[srcIndex + 0];
						sumG += source.Data[srcIndex + 1];
						sumB += source.Data[srcIndex + 2];
						sumA += source.Data[srcIndex + 3];
						sampleCount++;
					}
				}

				var destIndex = (y * targetWidth + x) * 4;
				result[destIndex + 0] = (byte)(sumR / sampleCount);
				result[destIndex + 1] = (byte)(sumG / sampleCount);
				result[destIndex + 2] = (byte)(sumB / sampleCount);
				result[destIndex + 3] = (byte)(sumA / sampleCount);
			}
		}

		return new TextureMipData(targetWidth, targetHeight, result);
	}
}
