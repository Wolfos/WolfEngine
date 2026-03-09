using WolfEngine.Importing;

namespace WolfEngine.AssetPipeline;

public static class TextureRawImageSerializer
{
	private static ReadOnlySpan<byte> Magic => "WETX"u8;
	public const int CurrentVersion = 1;

	public static void Write(string path, ImportedTexture texture)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new ArgumentException("Output path cannot be null or empty.", nameof(path));
		}

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
			writer.Write(texture.Channels);
			writer.Write(texture.IsSrgb ? (byte)1 : (byte)0);
			writer.Write(texture.PixelData.Length);
			writer.Write(texture.PixelData);
		}

		File.Move(tempPath, path, true);
	}

	public static ImportedTexture Read(string path, string name)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new ArgumentException("Input path cannot be null or empty.", nameof(path));
		}

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
		var channels = reader.ReadInt32();
		var isSrgb = reader.ReadByte() != 0;
		var pixelDataLength = reader.ReadInt32();
		var pixelData = reader.ReadBytes(pixelDataLength);

		return new ImportedTexture(name, width, height, channels, isSrgb, pixelData);
	}
}
