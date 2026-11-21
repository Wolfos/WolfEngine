using WolfEngine.Importing;
using WolfEngine.Rendering;

namespace WolfEngine.AssetPipeline;

/// <summary>
/// Reads and writes the decoded texture a texture import produces, before it is compiled for a backend.
/// </summary>
/// <remarks>
/// Version 1 held 8-bit RGBA 2D textures only. Version 2 adds the pixel format (8-bit or half-float HDR), the
/// dimension and each mip's slice count, so cube textures keep their six faces. Version 1 files are still read.
/// </remarks>
public static class ImportedTextureSerializer
{
	private static ReadOnlySpan<byte> Magic => "WEIT"u8;
	public const int CurrentVersion = 2;
	private const int LegacyRgba8Version = 1;
	private const int FirstVersionWithLayout = 2;

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
			writer.Write((int)texture.Format);
			writer.Write((int)texture.Dimension);
			writer.Write(texture.MipCount);
			for (var i = 0; i < texture.MipLevels.Length; i++)
			{
				var mip = texture.MipLevels[i];
				writer.Write(mip.Width);
				writer.Write(mip.Height);
				writer.Write(mip.Depth);
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
		if (version != CurrentVersion && version != LegacyRgba8Version)
		{
			throw new InvalidOperationException(
				$"Unsupported imported texture artifact version {version}. Expected {CurrentVersion}.");
		}

		var header = ReadHeader(reader, version);
		var mips = new TextureMipData[header.MipCount];
		for (var i = 0; i < header.MipCount; i++)
		{
			var mipHeader = ReadMipHeader(reader, version);
			mips[i] = new TextureMipData(mipHeader.Width, mipHeader.Height, reader.ReadBytes(mipHeader.Length), mipHeader.Depth);
		}

		return new ImportedTexture(
			name,
			header.Width,
			header.Height,
			header.IsSrgb,
			header.Semantic,
			mips,
			header.Format,
			header.Dimension);
	}

	/// <summary>
	/// Reads the largest mip no bigger than <paramref name="targetMaxDimension"/> as 8-bit RGBA for a thumbnail.
	/// HDR content is converted to sRGB with highlights clipped, and a cube shows its first face (+X).
	/// </summary>
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
			if (version != CurrentVersion && version != LegacyRgba8Version)
			{
				return false;
			}

			var header = ReadHeader(reader, version);
			if (header.MipCount <= 0)
			{
				return false;
			}

			if (header.Format != TextureFormat.Rgba8Unorm && header.Format != TextureFormat.Rgba16Float)
			{
				return false;
			}

			var bytesPerPixel = TextureFormatUtilities.GetBytesPerBlock(header.Format);
			for (var i = 0; i < header.MipCount; i++)
			{
				var mipHeader = ReadMipHeader(reader, version);
				var sliceLength = mipHeader.Width * mipHeader.Height * bytesPerPixel;
				if (mipHeader.Width <= 0 ||
				    mipHeader.Height <= 0 ||
				    mipHeader.Depth <= 0 ||
				    mipHeader.Length != sliceLength * mipHeader.Depth)
				{
					return false;
				}

				var maxDimension = Math.Max(mipHeader.Width, mipHeader.Height);
				var selected = maxDimension <= targetMaxDimension || i == header.MipCount - 1;
				if (selected)
				{
					var data = reader.ReadBytes(sliceLength);
					if (data.Length != sliceLength)
					{
						return false;
					}

					preview = header.Format == TextureFormat.Rgba16Float
						? new ImportedTextureMipPreview(
							mipHeader.Width,
							mipHeader.Height,
							IsSrgb: true,
							header.Semantic,
							HdrPixels.ToPreviewRgba8Srgb(data))
						: new ImportedTextureMipPreview(
							mipHeader.Width,
							mipHeader.Height,
							header.IsSrgb,
							header.Semantic,
							data);
					return true;
				}

				if (stream.CanSeek == false || stream.Position + mipHeader.Length > stream.Length)
				{
					return false;
				}

				stream.Seek(mipHeader.Length, SeekOrigin.Current);
			}

			return false;
		}
		catch
		{
			preview = default;
			return false;
		}
	}

	private static Header ReadHeader(BinaryReader reader, int version)
	{
		var width = reader.ReadInt32();
		var height = reader.ReadInt32();
		var isSrgb = reader.ReadByte() != 0;
		var semantic = (TextureSemantic)reader.ReadInt32();
		var format = TextureFormat.Rgba8Unorm;
		var dimension = TextureDimension.Texture2D;
		if (version >= FirstVersionWithLayout)
		{
			format = (TextureFormat)reader.ReadInt32();
			dimension = (TextureDimension)reader.ReadInt32();
		}

		var mipCount = reader.ReadInt32();

		return new Header(width, height, isSrgb, semantic, format, dimension, mipCount);
	}

	private static MipHeader ReadMipHeader(BinaryReader reader, int version)
	{
		var width = reader.ReadInt32();
		var height = reader.ReadInt32();
		var depth = version >= FirstVersionWithLayout
			? reader.ReadInt32()
			: 1;
		var length = reader.ReadInt32();

		return new MipHeader(width, height, depth, length);
	}

	private readonly record struct Header(
		int Width,
		int Height,
		bool IsSrgb,
		TextureSemantic Semantic,
		TextureFormat Format,
		TextureDimension Dimension,
		int MipCount);

	private readonly record struct MipHeader(
		int Width,
		int Height,
		int Depth,
		int Length);
}

public readonly record struct ImportedTextureMipPreview(
	int Width,
	int Height,
	bool IsSrgb,
	TextureSemantic Semantic,
	byte[] Data);
