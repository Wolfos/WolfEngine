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
}
