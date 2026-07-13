using System;
using System.IO;
using WolfEngine.Rendering;

namespace WolfEngine.AssetPipeline;

public static class TerrainAssetSerializer
{
	private static ReadOnlySpan<byte> Magic => "WETR"u8;
	public const int CurrentVersion = 1;

	public static void Write(string path, TerrainAsset terrainAsset)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(terrainAsset);

		var directory = Path.GetDirectoryName(path);
		if (string.IsNullOrWhiteSpace(directory) == false)
		{
			Directory.CreateDirectory(directory);
		}

		var tempPath = path + ".tmp";
		using (var stream = File.Create(tempPath))
		using (var writer = new BinaryWriter(stream))
		{
			writer.Write(Magic);
			writer.Write(CurrentVersion);
			WriteTexture(writer, terrainAsset.Heightmap);
			WriteTexture(writer, terrainAsset.LayerIndexMap);
			WriteTexture(writer, terrainAsset.LayerWeightMap);
		}

		File.Move(tempPath, path, true);
	}

	public static TerrainAsset Read(string path, string name)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		using var stream = File.OpenRead(path);
		return Read(stream, name);
	}

	public static TerrainAsset Read(Stream stream, string name)
	{
		ArgumentNullException.ThrowIfNull(stream);
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		using var reader = new BinaryReader(stream);
		var magic = reader.ReadBytes(Magic.Length);
		if (magic.AsSpan().SequenceEqual(Magic) == false)
		{
			throw new InvalidOperationException("Terrain asset has an invalid header.");
		}

		var version = reader.ReadInt32();
		if (version != CurrentVersion)
		{
			throw new InvalidOperationException($"Unsupported terrain asset version {version}. Expected {CurrentVersion}.");
		}

		var heightmap = ReadTexture(reader, $"{name}:height");
		var layerIndexMap = ReadTexture(reader, $"{name}:layer_indices");
		var layerWeightMap = ReadTexture(reader, $"{name}:layer_weights");
		return new TerrainAsset(name, heightmap, layerIndexMap, layerWeightMap);
	}

	private static void WriteTexture(BinaryWriter writer, Texture texture)
	{
		writer.Write(texture.Width);
		writer.Write(texture.Height);
		writer.Write(texture.IsSrgb ? (byte)1 : (byte)0);
		writer.Write((int)texture.Format);
		writer.Write(texture.MipCount);
		for (var i = 0; i < texture.MipLevels.Length; i++)
		{
			var mip = texture.MipLevels[i];
			writer.Write(mip.Width);
			writer.Write(mip.Height);
			writer.Write(mip.Data.Length);
			writer.Write(mip.Data);
		}
	}

	private static Texture ReadTexture(BinaryReader reader, string name)
	{
		var width = reader.ReadInt32();
		var height = reader.ReadInt32();
		var isSrgb = reader.ReadByte() != 0;
		var format = (TextureFormat)reader.ReadInt32();
		var mipCount = reader.ReadInt32();
		var mips = new TextureMipData[mipCount];
		for (var i = 0; i < mipCount; i++)
		{
			var mipWidth = reader.ReadInt32();
			var mipHeight = reader.ReadInt32();
			var length = reader.ReadInt32();
			mips[i] = new TextureMipData(mipWidth, mipHeight, reader.ReadBytes(length));
		}

		return new Texture(name, width, height, isSrgb, format, mips);
	}
}
