using WolfEngine.Importing;
using WolfEngine.Rendering;

namespace WolfEngine.AssetPipeline;

public static class ImportedTextureSerializer
{
	private static ReadOnlySpan<byte> Magic => "WEIT"u8;
	public const int CurrentVersion = 1;

	public static void Write(string path, ImportedTexture texture)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);

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
			writer.Write((int)texture.Semantic);
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

	public static ImportedTexture Read(string path, string name)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		if (File.Exists(path) == false)
		{
			throw new FileNotFoundException($"Imported texture artifact '{path}' was not found.", path);
		}

		using var stream = File.OpenRead(path);
		using var reader = new BinaryReader(stream);
		var magic = reader.ReadBytes(Magic.Length);
		if (magic.AsSpan().SequenceEqual(Magic) == false)
		{
			throw new InvalidOperationException($"Imported texture artifact '{path}' has an invalid header.");
		}

		var version = reader.ReadInt32();
		if (version != CurrentVersion)
		{
			throw new InvalidOperationException(
				$"Unsupported imported texture artifact version {version}. Expected {CurrentVersion}.");
		}

		var width = reader.ReadInt32();
		var height = reader.ReadInt32();
		var isSrgb = reader.ReadByte() != 0;
		var semantic = (TextureSemantic)reader.ReadInt32();
		var mipCount = reader.ReadInt32();
		var mips = new TextureMipData[mipCount];
		for (var i = 0; i < mipCount; i++)
		{
			var mipWidth = reader.ReadInt32();
			var mipHeight = reader.ReadInt32();
			var length = reader.ReadInt32();
			mips[i] = new TextureMipData(mipWidth, mipHeight, reader.ReadBytes(length));
		}

		return new ImportedTexture(name, width, height, isSrgb, semantic, mips);
	}

	public static bool TryReadMip(string path, int targetMaxDimension, out ImportedTextureMipPreview preview)
	{
		preview = default;
		if (string.IsNullOrWhiteSpace(path) || targetMaxDimension <= 0 || File.Exists(path) == false)
		{
			return false;
		}

		try
		{
			using var stream = File.OpenRead(path);
			using var reader = new BinaryReader(stream);
			var magic = reader.ReadBytes(Magic.Length);
			if (magic.AsSpan().SequenceEqual(Magic) == false)
			{
				return false;
			}

			var version = reader.ReadInt32();
			if (version != CurrentVersion)
			{
				return false;
			}

			_ = reader.ReadInt32();
			_ = reader.ReadInt32();
			var isSrgb = reader.ReadByte() != 0;
			var semantic = (TextureSemantic)reader.ReadInt32();
			var mipCount = reader.ReadInt32();
			if (mipCount <= 0)
			{
				return false;
			}

			for (var i = 0; i < mipCount; i++)
			{
				var mipWidth = reader.ReadInt32();
				var mipHeight = reader.ReadInt32();
				var length = reader.ReadInt32();
				if (mipWidth <= 0 || mipHeight <= 0 || length != mipWidth * mipHeight * 4)
				{
					return false;
				}

				var maxDimension = Math.Max(mipWidth, mipHeight);
				var selected = maxDimension <= targetMaxDimension || i == mipCount - 1;
				if (selected)
				{
					var data = reader.ReadBytes(length);
					if (data.Length != length)
					{
						return false;
					}

					preview = new ImportedTextureMipPreview(mipWidth, mipHeight, isSrgb, semantic, data);
					return true;
				}

				if (stream.CanSeek == false || stream.Position + length > stream.Length)
				{
					return false;
				}

				stream.Seek(length, SeekOrigin.Current);
			}

			return false;
		}
		catch
		{
			preview = default;
			return false;
		}
	}
}

public readonly record struct ImportedTextureMipPreview(
	int Width,
	int Height,
	bool IsSrgb,
	TextureSemantic Semantic,
	byte[] Data);
