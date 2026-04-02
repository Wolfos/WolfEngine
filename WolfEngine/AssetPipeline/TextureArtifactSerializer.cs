using System;
using System.IO;
using WolfEngine.Importing;
using WolfEngine.Rendering;

namespace WolfEngine.AssetPipeline;

public static class TextureArtifactSerializer
{
	private static ReadOnlySpan<byte> Magic => "WETX"u8;
	public const int CurrentVersion = 2;

	public static void Write(string path, Texture texture, TextureSemantic semantic, TextureCompressionFamily compressionFamily)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(texture);

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
			writer.Write(texture.Width);
			writer.Write(texture.Height);
			writer.Write(texture.IsSrgb ? (byte)1 : (byte)0);
			writer.Write((int)semantic);
			writer.Write((int)texture.Format);
			writer.Write((int)compressionFamily);
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

		File.Move(tempPath, path, true);
	}

	public static Texture Read(string path, string name)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		if (File.Exists(path) == false)
		{
			throw new FileNotFoundException($"Texture artifact '{path}' was not found.", path);
		}

		using var stream = File.OpenRead(path);
		using var reader = new BinaryReader(stream);
		var magic = reader.ReadBytes(Magic.Length);
		if (magic.AsSpan().SequenceEqual(Magic) == false)
		{
			throw new InvalidOperationException($"Texture artifact '{path}' has an invalid header.");
		}

		var version = reader.ReadInt32();
		if (version != CurrentVersion)
		{
			throw new InvalidOperationException(
				$"Unsupported texture artifact version {version}. Expected {CurrentVersion}.");
		}

		var width = reader.ReadInt32();
		var height = reader.ReadInt32();
		var isSrgb = reader.ReadByte() != 0;
		_ = (TextureSemantic)reader.ReadInt32();
		var format = (TextureFormat)reader.ReadInt32();
		_ = (TextureCompressionFamily)reader.ReadInt32();
		var mipCount = reader.ReadInt32();
		var mipLevels = new TextureMipData[mipCount];
		for (var i = 0; i < mipCount; i++)
		{
			var mipWidth = reader.ReadInt32();
			var mipHeight = reader.ReadInt32();
			var length = reader.ReadInt32();
			mipLevels[i] = new TextureMipData(mipWidth, mipHeight, reader.ReadBytes(length));
		}

		return new Texture(name, width, height, isSrgb, format, mipLevels);
	}
}
