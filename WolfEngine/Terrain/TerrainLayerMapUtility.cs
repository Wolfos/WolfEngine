using System;
using System.Collections.Generic;
using System.Linq;
using WolfEngine.Rendering;

namespace WolfEngine;

public static class TerrainLayerMapUtility
{
	public static TextureMipData[] GenerateLayerIndexMipChain(TextureMipData baseIndexMip, TextureMipData baseWeightMip)
	{
		return GenerateLayerMipChain(baseIndexMip, baseWeightMip).Indices;
	}

	public static TextureMipData[] GenerateLayerWeightMipChain(TextureMipData baseIndexMip, TextureMipData baseWeightMip)
	{
		return GenerateLayerMipChain(baseIndexMip, baseWeightMip).Weights;
	}

	public static (TextureMipData[] Indices, TextureMipData[] Weights) GenerateLayerMipChain(TextureMipData baseIndexMip, TextureMipData baseWeightMip)
	{
		ValidateMipPair(baseIndexMip, baseWeightMip);
		var indices = new List<TextureMipData> { CloneMip(baseIndexMip) };
		var weights = new List<TextureMipData> { CloneMip(baseWeightMip) };
		var currentIndex = indices[0];
		var currentWeight = weights[0];

		while (currentIndex.Width > 1 || currentIndex.Height > 1)
		{
			(currentIndex, currentWeight) = Downsample(currentIndex, currentWeight);
			indices.Add(currentIndex);
			weights.Add(currentWeight);
		}

		return (indices.ToArray(), weights.ToArray());
	}

	public static void NormalizePixel(byte[] indices, byte[] weights, int pixelIndex, int fallbackLayer = 0)
	{
		var offset = pixelIndex * 4;
		var sum = weights[offset] + weights[offset + 1] + weights[offset + 2] + weights[offset + 3];
		if (sum <= 0)
		{
			indices[offset] = (byte)Math.Clamp(fallbackLayer, 0, 255);
			indices[offset + 1] = 0;
			indices[offset + 2] = 0;
			indices[offset + 3] = 0;
			weights[offset] = 255;
			weights[offset + 1] = 0;
			weights[offset + 2] = 0;
			weights[offset + 3] = 0;
			return;
		}

		var remaining = 255;
		for (var channel = 0; channel < 4; channel++)
		{
			var value = channel == 3
				? remaining
				: Math.Clamp((int)MathF.Round(weights[offset + channel] / (float)sum * 255.0f), 0, remaining);
			weights[offset + channel] = (byte)value;
			remaining -= value;
		}
	}

	private static (TextureMipData Indices, TextureMipData Weights) Downsample(TextureMipData sourceIndices, TextureMipData sourceWeights)
	{
		var width = Math.Max(1, sourceIndices.Width / 2);
		var height = Math.Max(1, sourceIndices.Height / 2);
		var indices = new byte[width * height * 4];
		var weights = new byte[width * height * 4];

		for (var y = 0; y < height; y++)
		{
			for (var x = 0; x < width; x++)
			{
				var layerTotals = new Dictionary<byte, int>();
				for (var oy = 0; oy < 2; oy++)
				{
					for (var ox = 0; ox < 2; ox++)
					{
						var sx = Math.Min(sourceIndices.Width - 1, x * 2 + ox);
						var sy = Math.Min(sourceIndices.Height - 1, y * 2 + oy);
						var sourceOffset = ((sy * sourceIndices.Width) + sx) * 4;
						for (var channel = 0; channel < 4; channel++)
						{
							var layer = sourceIndices.Data[sourceOffset + channel];
							var weight = sourceWeights.Data[sourceOffset + channel];
							if (weight == 0)
							{
								continue;
							}

							layerTotals[layer] = layerTotals.TryGetValue(layer, out var total) ? total + weight : weight;
						}
					}
				}

				var destinationOffset = ((y * width) + x) * 4;
				var topLayers = layerTotals
					.OrderByDescending(pair => pair.Value)
					.ThenBy(pair => pair.Key)
					.Take(4)
					.ToArray();
				if (topLayers.Length == 0)
				{
					weights[destinationOffset] = 255;
					continue;
				}

				var totalWeight = topLayers.Sum(pair => pair.Value);
				var remaining = 255;
				for (var i = 0; i < topLayers.Length; i++)
				{
					indices[destinationOffset + i] = topLayers[i].Key;
					var value = i == topLayers.Length - 1
						? remaining
						: Math.Clamp((int)MathF.Round(topLayers[i].Value / (float)totalWeight * 255.0f), 0, remaining);
					weights[destinationOffset + i] = (byte)value;
					remaining -= value;
				}
			}
		}

		return (new TextureMipData(width, height, indices), new TextureMipData(width, height, weights));
	}

	private static void ValidateMipPair(TextureMipData indexMip, TextureMipData weightMip)
	{
		if (indexMip.Width != weightMip.Width || indexMip.Height != weightMip.Height)
		{
			throw new ArgumentException("Layer index and weight mips must have matching dimensions.");
		}

		if (indexMip.Data.Length != indexMip.Width * indexMip.Height * 4 ||
		    weightMip.Data.Length != weightMip.Width * weightMip.Height * 4)
		{
			throw new ArgumentException("Layer index and weight mips must be four bytes per pixel.");
		}
	}

	private static TextureMipData CloneMip(TextureMipData mip)
	{
		return new TextureMipData(mip.Width, mip.Height, mip.Data.ToArray());
	}
}
