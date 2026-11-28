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
	/// <param name="alphaCoverageCutoff">
	/// When above zero, each 8-bit mip's alpha is scaled so the fraction of texels at or above this cutoff matches the
	/// base mip's (Castaño, "Computing Alpha Mipmaps", 2010). Filtering continues from the unscaled mips, so scales do
	/// not compound down the chain.
	/// </param>
	public static TextureMipData[] GenerateMipChain(TextureMipData baseMip, TextureFormat format, float alphaCoverageCutoff = 0.0f)
	{
		ArgumentNullException.ThrowIfNull(baseMip.Data);
		if (format != TextureFormat.Rgba8Unorm && format != TextureFormat.Rgba16Float)
		{
			throw new NotSupportedException($"Mip generation does not support {format}.");
		}

		var preserveCoverage = alphaCoverageCutoff > 0.0f && format == TextureFormat.Rgba8Unorm;
		var cutoff = (byte)Math.Clamp(MathF.Round(alphaCoverageCutoff * 255.0f), 1, 255);
		var targetCoverage = preserveCoverage ? GetAlphaCoverage(baseMip.Data, cutoff, 1.0f) : 0.0f;

		var mips = new List<TextureMipData> { baseMip };
		var current = baseMip;
		while (current.Width > 1 || current.Height > 1)
		{
			current = format == TextureFormat.Rgba16Float
				? DownsampleRgba16Float(current)
				: DownsampleRgba32(current);
			mips.Add(preserveCoverage ? ScaleAlphaToCoverage(current, cutoff, targetCoverage) : current);
		}

		return [.. mips];
	}

	private const int CoverageSearchSteps = 16;
	private const float MaxCoverageScale = 64.0f;

	/// <summary>
	/// A copy of the mip with alpha scaled by the factor, found by bisection, whose coverage at the cutoff is closest to
	/// the target. Coverage only grows with the scale, so bisection finds it.
	/// </summary>
	private static TextureMipData ScaleAlphaToCoverage(TextureMipData mip, byte cutoff, float targetCoverage)
	{
		var low = 0.0f;
		var high = MaxCoverageScale;
		for (var step = 0; step < CoverageSearchSteps; step++)
		{
			var middle = (low + high) * 0.5f;
			if (GetAlphaCoverage(mip.Data, cutoff, middle) < targetCoverage)
			{
				low = middle;
			}
			else
			{
				high = middle;
			}
		}

		var scale = MathF.Abs(GetAlphaCoverage(mip.Data, cutoff, low) - targetCoverage) <
		            MathF.Abs(GetAlphaCoverage(mip.Data, cutoff, high) - targetCoverage)
			? low
			: high;
		var scaled = (byte[])mip.Data.Clone();
		for (var i = 3; i < scaled.Length; i += 4)
		{
			scaled[i] = ScaleAlpha(scaled[i], scale);
		}

		return new TextureMipData(mip.Width, mip.Height, scaled, mip.Depth);
	}

	/// <summary>The fraction of texels whose alpha, scaled, is at or above the cutoff: what alpha testing keeps.</summary>
	internal static float GetAlphaCoverage(byte[] rgba, byte cutoff, float scale)
	{
		var texelCount = rgba.Length / 4;
		if (texelCount == 0) return 0.0f;

		var covered = 0;
		for (var i = 3; i < rgba.Length; i += 4)
		{
			if (ScaleAlpha(rgba[i], scale) >= cutoff)
			{
				covered++;
			}
		}

		return covered / (float)texelCount;
	}

	private static byte ScaleAlpha(byte alpha, float scale)
	{
		return (byte)Math.Clamp(MathF.Round(alpha * scale), 0.0f, 255.0f);
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
