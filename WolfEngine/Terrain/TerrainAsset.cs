using System;
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
