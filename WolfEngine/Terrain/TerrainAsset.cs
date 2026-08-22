using WolfEngine.AssetPipeline;
using WolfEngine.Rendering;

namespace WolfEngine;

[RuntimeAsset(AssetType.Terrain, typeof(TerrainAsset), typeof(ITerrainAssetRuntimeResolver))]
public sealed class TerrainAsset
{
	public const string FileExtension = ".terrain";

	public TerrainAsset(
		string name,
		Texture heightmap,
		Texture layerIndexMap,
		Texture layerWeightMap)
	{
		Name = name ?? throw new ArgumentNullException(nameof(name));
		Heightmap = heightmap ?? throw new ArgumentNullException(nameof(heightmap));
		LayerIndexMap = layerIndexMap ?? throw new ArgumentNullException(nameof(layerIndexMap));
		LayerWeightMap = layerWeightMap ?? throw new ArgumentNullException(nameof(layerWeightMap));
		Validate();
	}

	public string Name { get; }
	public Texture Heightmap { get; private set; }
	public Texture LayerIndexMap { get; private set; }
	public Texture LayerWeightMap { get; private set; }

	public int HeightmapWidth => Heightmap.Width;
	public int HeightmapHeight => Heightmap.Height;
	public int LayerMapWidth => LayerIndexMap.Width;
	public int LayerMapHeight => LayerIndexMap.Height;

	public void ApplyMaps(Texture heightmap, Texture layerIndexMap, Texture layerWeightMap)
	{
		ArgumentNullException.ThrowIfNull(heightmap);
		ArgumentNullException.ThrowIfNull(layerIndexMap);
		ArgumentNullException.ThrowIfNull(layerWeightMap);

		Heightmap.ApplyTextureData(
			heightmap.Width,
			heightmap.Height,
			heightmap.IsSrgb,
			heightmap.Format,
			CloneMipLevels(heightmap.MipLevels));
		LayerIndexMap.ApplyTextureData(
			layerIndexMap.Width,
			layerIndexMap.Height,
			layerIndexMap.IsSrgb,
			layerIndexMap.Format,
			CloneMipLevels(layerIndexMap.MipLevels));
		LayerWeightMap.ApplyTextureData(
			layerWeightMap.Width,
			layerWeightMap.Height,
			layerWeightMap.IsSrgb,
			layerWeightMap.Format,
			CloneMipLevels(layerWeightMap.MipLevels));
		Validate();
	}

	public void ResizeMaps(int heightmapResolution, int layerMapResolution)
	{
		heightmapResolution = Math.Max(heightmapResolution, 2);
		layerMapResolution = Math.Max(layerMapResolution, 1);
		if (Heightmap.Width == heightmapResolution && Heightmap.Height == heightmapResolution &&
		    LayerIndexMap.Width == layerMapResolution && LayerIndexMap.Height == layerMapResolution)
		{
			return;
		}

		var heightmap = CreateResampledHeightmap(Heightmap, heightmapResolution, heightmapResolution);
		var (layerIndices, layerWeights) = CreateResampledLayerMaps(LayerIndexMap, LayerWeightMap, layerMapResolution, layerMapResolution);
		ApplyMaps(heightmap, layerIndices, layerWeights);
	}

	public TerrainAssetSnapshot CaptureSnapshot(Guid assetId)
	{
		return new TerrainAssetSnapshot(
			assetId,
			Name,
			CloneTexture(Heightmap),
			CloneTexture(LayerIndexMap),
			CloneTexture(LayerWeightMap));
	}

	private void Validate()
	{
		if (Heightmap.Format != TextureFormat.R16Unorm)
		{
			throw new InvalidOperationException("Terrain heightmap must use R16Unorm.");
		}

		if (LayerIndexMap.Format != TextureFormat.Rgba8Uint)
		{
			throw new InvalidOperationException("Terrain layer index map must use Rgba8Uint.");
		}

		if (LayerWeightMap.Format != TextureFormat.Rgba8Unorm)
		{
			throw new InvalidOperationException("Terrain layer weight map must use Rgba8Unorm.");
		}

		if (Heightmap.MipLevels.Length != 1)
		{
			throw new InvalidOperationException("Terrain heightmap must contain only its top mip.");
		}

		if (LayerIndexMap.Width != LayerWeightMap.Width ||
		    LayerIndexMap.Height != LayerWeightMap.Height ||
		    LayerIndexMap.MipLevels.Length != LayerWeightMap.MipLevels.Length)
		{
			throw new InvalidOperationException("Terrain layer index and weight maps must have matching dimensions and mip counts.");
		}
	}

	public static TerrainAsset CreateDefault(string name, int heightmapWidth = 257, int heightmapHeight = 257, int layerMapWidth = 256, int layerMapHeight = 256)
	{
		heightmapWidth = Math.Max(heightmapWidth, 2);
		heightmapHeight = Math.Max(heightmapHeight, 2);
		layerMapWidth = Math.Max(layerMapWidth, 1);
		layerMapHeight = Math.Max(layerMapHeight, 1);

		var heightData = new byte[heightmapWidth * heightmapHeight * 2];
		var indices = new byte[layerMapWidth * layerMapHeight * 4];
		var weights = new byte[layerMapWidth * layerMapHeight * 4];
		for (var i = 0; i < layerMapWidth * layerMapHeight; i++)
		{
			weights[i * 4] = 255;
		}

		return new TerrainAsset(
			name,
			new Texture($"{name}:height", heightmapWidth, heightmapHeight, false, TextureFormat.R16Unorm, [new TextureMipData(heightmapWidth, heightmapHeight, heightData)]),
			new Texture($"{name}:layer_indices", layerMapWidth, layerMapHeight, false, TextureFormat.Rgba8Uint, TerrainLayerMapUtility.GenerateLayerIndexMipChain(new TextureMipData(layerMapWidth, layerMapHeight, indices), new TextureMipData(layerMapWidth, layerMapHeight, weights))),
			new Texture($"{name}:layer_weights", layerMapWidth, layerMapHeight, false, TextureFormat.Rgba8Unorm, TerrainLayerMapUtility.GenerateLayerWeightMipChain(new TextureMipData(layerMapWidth, layerMapHeight, indices), new TextureMipData(layerMapWidth, layerMapHeight, weights))));
	}

	public static Texture CloneTexture(Texture source)
	{
		return new Texture(
			source.Name,
			source.Width,
			source.Height,
			source.IsSrgb,
			source.Format,
			CloneMipLevels(source.MipLevels));
	}

	private static Texture CreateResampledHeightmap(Texture source, int targetWidth, int targetHeight)
	{
		var sourceMip = source.MipLevels[0];
		var data = new byte[targetWidth * targetHeight * 2];
		for (var y = 0; y < targetHeight; y++)
		{
			var sourceY = GetResampleCoordinate(y, targetHeight, sourceMip.Height);
			for (var x = 0; x < targetWidth; x++)
			{
				var sourceX = GetResampleCoordinate(x, targetWidth, sourceMip.Width);
				var value = SampleR16Bilinear(sourceMip, sourceX, sourceY);
				var offset = ((y * targetWidth) + x) * 2;
				data[offset] = (byte)value;
				data[offset + 1] = (byte)(value >> 8);
			}
		}

		return new Texture(source.Name, targetWidth, targetHeight, false, TextureFormat.R16Unorm,
			[new TextureMipData(targetWidth, targetHeight, data)]);
	}

	private static (Texture Indices, Texture Weights) CreateResampledLayerMaps(
		Texture sourceIndices,
		Texture sourceWeights,
		int targetWidth,
		int targetHeight)
	{
		var sourceIndexMip = sourceIndices.MipLevels[0];
		var sourceWeightMip = sourceWeights.MipLevels[0];
		var indices = new byte[targetWidth * targetHeight * 4];
		var weights = new byte[targetWidth * targetHeight * 4];
		for (var y = 0; y < targetHeight; y++)
		{
			var sourceY = GetResampleCoordinate(y, targetHeight, sourceIndexMip.Height);
			for (var x = 0; x < targetWidth; x++)
			{
				var sourceX = GetResampleCoordinate(x, targetWidth, sourceIndexMip.Width);
				ResampleLayerPixel(sourceIndexMip, sourceWeightMip, sourceX, sourceY, indices, weights, y * targetWidth + x);
			}
		}

		var indexMip = new TextureMipData(targetWidth, targetHeight, indices);
		var weightMip = new TextureMipData(targetWidth, targetHeight, weights);
		var mips = TerrainLayerMapUtility.GenerateLayerMipChain(indexMip, weightMip);
		return (
			new Texture(sourceIndices.Name, targetWidth, targetHeight, false, TextureFormat.Rgba8Uint, mips.Indices),
			new Texture(sourceWeights.Name, targetWidth, targetHeight, false, TextureFormat.Rgba8Unorm, mips.Weights));
	}

	private static void ResampleLayerPixel(
		TextureMipData sourceIndices,
		TextureMipData sourceWeights,
		float sourceX,
		float sourceY,
		byte[] destinationIndices,
		byte[] destinationWeights,
		int destinationPixel)
	{
		var x0 = Math.Clamp((int)MathF.Floor(sourceX), 0, sourceIndices.Width - 1);
		var y0 = Math.Clamp((int)MathF.Floor(sourceY), 0, sourceIndices.Height - 1);
		var x1 = Math.Min(x0 + 1, sourceIndices.Width - 1);
		var y1 = Math.Min(y0 + 1, sourceIndices.Height - 1);
		var tx = sourceX - x0;
		var ty = sourceY - y0;
		var layerWeights = new Dictionary<byte, float>();
		AccumulateLayerPixel(x0, y0, (1.0f - tx) * (1.0f - ty));
		AccumulateLayerPixel(x1, y0, tx * (1.0f - ty));
		AccumulateLayerPixel(x0, y1, (1.0f - tx) * ty);
		AccumulateLayerPixel(x1, y1, tx * ty);

		var offset = destinationPixel * 4;
		var strongestLayers = layerWeights
			.Where(pair => pair.Value > 0.0f)
			.OrderByDescending(pair => pair.Value)
			.ThenBy(pair => pair.Key)
			.Take(4)
			.ToArray();
		if (strongestLayers.Length == 0)
		{
			destinationWeights[offset] = 255;
			return;
		}

		var totalWeight = strongestLayers.Sum(pair => pair.Value);
		var remaining = 255;
		for (var i = 0; i < strongestLayers.Length; i++)
		{
			destinationIndices[offset + i] = strongestLayers[i].Key;
			var weight = i == strongestLayers.Length - 1
				? remaining
				: Math.Clamp((int)MathF.Round(strongestLayers[i].Value / totalWeight * 255.0f), 0, remaining);
			destinationWeights[offset + i] = (byte)weight;
			remaining -= weight;
		}

		void AccumulateLayerPixel(int x, int y, float bilinearWeight)
		{
			var sourceOffset = ((y * sourceIndices.Width) + x) * 4;
			for (var channel = 0; channel < 4; channel++)
			{
				var weight = sourceWeights.Data[sourceOffset + channel];
				if (weight == 0)
				{
					continue;
				}

				var layer = sourceIndices.Data[sourceOffset + channel];
				layerWeights[layer] = layerWeights.TryGetValue(layer, out var total)
					? total + weight * bilinearWeight
					: weight * bilinearWeight;
			}
		}
	}

	private static ushort SampleR16Bilinear(TextureMipData source, float sourceX, float sourceY)
	{
		var x0 = Math.Clamp((int)MathF.Floor(sourceX), 0, source.Width - 1);
		var y0 = Math.Clamp((int)MathF.Floor(sourceY), 0, source.Height - 1);
		var x1 = Math.Min(x0 + 1, source.Width - 1);
		var y1 = Math.Min(y0 + 1, source.Height - 1);
		var tx = sourceX - x0;
		var ty = sourceY - y0;
		var top = Lerp(ReadR16(source, x0, y0), ReadR16(source, x1, y0), tx);
		var bottom = Lerp(ReadR16(source, x0, y1), ReadR16(source, x1, y1), tx);
		return (ushort)Math.Clamp((int)MathF.Round(Lerp(top, bottom, ty)), 0, ushort.MaxValue);
	}

	private static ushort ReadR16(TextureMipData source, int x, int y)
	{
		var offset = ((y * source.Width) + x) * 2;
		return (ushort)(source.Data[offset] | (source.Data[offset + 1] << 8));
	}

	private static float GetResampleCoordinate(int targetCoordinate, int targetSize, int sourceSize)
	{
		return targetSize <= 1 || sourceSize <= 1
			? 0.0f
			: targetCoordinate / (float)(targetSize - 1) * (sourceSize - 1);
	}

	private static float Lerp(float a, float b, float t) => a + (b - a) * t;

	public static TextureMipData[] CloneMipLevels(TextureMipData[] mipLevels)
	{
		var clone = new TextureMipData[mipLevels.Length];
		for (var i = 0; i < mipLevels.Length; i++)
		{
			var mip = mipLevels[i];
			clone[i] = new TextureMipData(mip.Width, mip.Height, mip.Data.ToArray());
		}

		return clone;
	}
}

public readonly record struct TerrainAssetSnapshot(
	Guid AssetId,
	string Name,
	Texture Heightmap,
	Texture LayerIndexMap,
	Texture LayerWeightMap);
