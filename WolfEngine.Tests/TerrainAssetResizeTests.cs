using WolfEngine.Rendering;

namespace WolfEngine.Tests;

public sealed class TerrainAssetResizeTests
{
	[Test]
	public void ResizeMaps_BilinearlyResamplesHeightmap()
	{
		var terrain = CreateTerrain(2, 2, [0, 0, 0, 0, 0, 0, 255, 255]);

		terrain.ResizeMaps(4, 2);

		Assert.That(terrain.HeightmapWidth, Is.EqualTo(4));
		Assert.That(ReadHeight(terrain.Heightmap, 1, 1), Is.EqualTo(7282).Within(1));
		Assert.That(ReadHeight(terrain.Heightmap, 2, 2), Is.EqualTo(29127).Within(1));
	}

	[Test]
	public void ResizeMaps_BilinearlyBlendsSplatLayersAndRegeneratesMips()
	{
		var terrain = CreateTerrain(2, 2, new byte[8]);
		var indices = terrain.LayerIndexMap.MipLevels[0].Data;
		var weights = terrain.LayerWeightMap.MipLevels[0].Data;
		SetLayer(indices, weights, 0, 0, 1);
		SetLayer(indices, weights, 1, 0, 2);
		SetLayer(indices, weights, 0, 1, 3);
		SetLayer(indices, weights, 1, 1, 4);

		terrain.ResizeMaps(2, 4);

		var offset = ((1 * 4) + 1) * 4;
		Assert.That(terrain.LayerIndexMap.MipCount, Is.EqualTo(3));
		Assert.That(terrain.LayerWeightMap.MipCount, Is.EqualTo(3));
		Assert.That(terrain.LayerWeightMap.MipLevels[0].Data.Skip(offset).Take(4).Sum(value => value), Is.EqualTo(255));
		Assert.That(terrain.LayerIndexMap.MipLevels[0].Data.Skip(offset).Take(4), Is.EquivalentTo(new byte[] { 1, 2, 3, 4 }));
		Assert.That(terrain.LayerWeightMap.MipLevels[0].Data.Skip(offset).Take(4).Any(value => value is > 0 and < 255), Is.True);
	}

	private static TerrainAsset CreateTerrain(int width, int height, byte[] heightData)
	{
		var indexData = new byte[width * height * 4];
		var weightData = new byte[width * height * 4];
		for (var pixel = 0; pixel < width * height; pixel++)
		{
			weightData[pixel * 4] = 255;
		}

		var indexMip = new TextureMipData(width, height, indexData);
		var weightMip = new TextureMipData(width, height, weightData);
		var mips = TerrainLayerMapUtility.GenerateLayerMipChain(indexMip, weightMip);
		return new TerrainAsset("terrain",
			new Texture("height", width, height, false, TextureFormat.R16Unorm, [new TextureMipData(width, height, heightData)]),
			new Texture("indices", width, height, false, TextureFormat.Rgba8Uint, mips.Indices),
			new Texture("weights", width, height, false, TextureFormat.Rgba8Unorm, mips.Weights));
	}

	private static void SetLayer(byte[] indices, byte[] weights, int x, int y, byte layer)
	{
		var offset = ((y * 2) + x) * 4;
		indices[offset] = layer;
		weights[offset] = 255;
	}

	private static ushort ReadHeight(Texture texture, int x, int y)
	{
		var data = texture.MipLevels[0].Data;
		var offset = ((y * texture.Width) + x) * 2;
		return (ushort)(data[offset] | (data[offset + 1] << 8));
	}
}
