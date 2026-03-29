using System;
using StbImageSharp;
using File = System.IO.File;

namespace WolfEngine.Importing;

public sealed class StbImageLoader : IImageLoader
{
	public ImportedTexture Load(string path, TextureSemantic semantic)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new ArgumentException("Path cannot be null or empty.", nameof(path));
		}

		var data = File.ReadAllBytes(path);
		return Decode(data, semantic, path);
	}

	public ImportedTexture LoadEmbedded(byte[] data, TextureSemantic semantic, string nameHint)
	{
		if (data is null || data.Length == 0)
		{
			throw new ArgumentException("Embedded texture data cannot be null or empty.", nameof(data));
		}

		return Decode(data, semantic, nameHint);
	}

	private static ImportedTexture Decode(byte[] data, TextureSemantic semantic, string nameHint)
	{
		var image = ImageResult.FromMemory(data, ColorComponents.RedGreenBlueAlpha);

		return new ImportedTexture(
			nameHint,
			image.Width,
			image.Height,
			(int)image.Comp,
			IsSrgb(semantic),
			image.Data);
	}

	public static bool IsSrgb(TextureSemantic semantic) =>
		semantic is TextureSemantic.BaseColor or TextureSemantic.Emissive;
}
