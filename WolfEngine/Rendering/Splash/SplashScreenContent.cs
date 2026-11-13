using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace WolfEngine.Rendering.Splash;

/// <summary>
/// The baked engine splash screen: the logo and the "Powered by WolfEngine" lines as distance fields, laid out in
/// reference units. A reference unit is one pixel on a 1080-pixel-tall screen; the renderer scales the layout to
/// the window. The origin is the screen centre and Y points down.
/// </summary>
/// <remarks>
/// Every distance is in the layer's texels and is negative inside the shape. The build bakes this from the
/// engine's vector logo and font (see <c>SplashScreenBaker</c> in the editor tooling); the runtime only reads it.
/// </remarks>
public sealed class SplashScreenContent
{
	/// <summary>
	/// RGBA16F. R: the silhouette, which is also the traced outline. G: the highlight shapes. B: details drawn in
	/// the silhouette colour over the highlight. A: how far along the trace the nearest outline point is, from 0 at
	/// the start to 1 where the two halves of the trace meet.
	/// </summary>
	public required SplashScreenLayer Logo { get; init; }

	/// <summary>RG16F. R: the "Powered by" line. G: the "WOLFENGINE" line.</summary>
	public required SplashScreenLayer Text { get; init; }

	/// <summary>The silhouette and detail fill, display-encoded (sRGB).</summary>
	public required ColorRGBA SilhouetteColor { get; init; }

	/// <summary>The highlight fill, display-encoded (sRGB).</summary>
	public required ColorRGBA HighlightColor { get; init; }

	/// <summary>The colour of both text lines, display-encoded (sRGB).</summary>
	public required ColorRGBA TextColor { get; init; }
}

/// <summary>One distance-field texture of the splash screen and where it sits in the layout.</summary>
public sealed class SplashScreenLayer
{
	/// <summary>The centre of the texture's rectangle, in reference units from the screen centre.</summary>
	public required Vector2 Center { get; init; }

	/// <summary>The size of the texture's rectangle, in reference units.</summary>
	public required Vector2 Size { get; init; }

	public required int Width { get; init; }

	public required int Height { get; init; }

	public required TextureFormat Format { get; init; }

	/// <summary>Rows top-down, tightly packed.</summary>
	public required byte[] Texels { get; init; }

	/// <summary>How many texels one reference unit spans.</summary>
	public float TexelsPerUnit => Width / Size.X;
}

/// <summary>Reads and writes the <c>SplashScreen</c> entry of a cooked game's bootstrap pack.</summary>
public static class SplashScreenSerializer
{
	public const string Kind = "SplashScreen";

	private const uint Magic = 0x4C505357; // "WSPL", little-endian.
	private const int Version = 1;

	public static byte[] Write(SplashScreenContent content)
	{
		ArgumentNullException.ThrowIfNull(content);

		using var stream = new MemoryStream();
		using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
		{
			writer.Write(Magic);
			writer.Write(Version);
			WriteColor(writer, content.SilhouetteColor);
			WriteColor(writer, content.HighlightColor);
			WriteColor(writer, content.TextColor);
			WriteLayer(writer, content.Logo);
			WriteLayer(writer, content.Text);
		}

		return stream.ToArray();
	}

	public static SplashScreenContent Read(ReadOnlySpan<byte> data)
	{
		var reader = new SpanReader(data);
		if (reader.ReadUInt32() != Magic)
		{
			throw new InvalidDataException("The splash screen data is not a WolfEngine splash screen.");
		}

		var version = reader.ReadInt32();
		if (version != Version)
		{
			throw new InvalidDataException($"Splash screen version {version} is not supported; expected {Version}.");
		}

		var silhouetteColor = ReadColor(ref reader);
		var highlightColor = ReadColor(ref reader);
		var textColor = ReadColor(ref reader);
		var logo = ReadLayer(ref reader, TextureFormat.Rgba16Float);
		var text = ReadLayer(ref reader, TextureFormat.Rg16Float);

		return new SplashScreenContent
		{
			Logo = logo,
			Text = text,
			SilhouetteColor = silhouetteColor,
			HighlightColor = highlightColor,
			TextColor = textColor
		};
	}

	private static void WriteColor(BinaryWriter writer, ColorRGBA color)
	{
		writer.Write(color.R);
		writer.Write(color.G);
		writer.Write(color.B);
		writer.Write(color.A);
	}

	private static void WriteLayer(BinaryWriter writer, SplashScreenLayer layer)
	{
		ValidateLayer(layer, layer.Format);

		writer.Write(layer.Center.X);
		writer.Write(layer.Center.Y);
		writer.Write(layer.Size.X);
		writer.Write(layer.Size.Y);
		writer.Write(layer.Width);
		writer.Write(layer.Height);
		writer.Write((int)layer.Format);
		writer.Write(layer.Texels.Length);
		writer.Write(layer.Texels);
	}

	private static ColorRGBA ReadColor(ref SpanReader reader)
	{
		var r = reader.ReadSingle();
		var g = reader.ReadSingle();
		var b = reader.ReadSingle();
		var a = reader.ReadSingle();

		return new ColorRGBA(r, g, b, a);
	}

	private static SplashScreenLayer ReadLayer(ref SpanReader reader, TextureFormat expectedFormat)
	{
		var center = new Vector2(reader.ReadSingle(), reader.ReadSingle());
		var size = new Vector2(reader.ReadSingle(), reader.ReadSingle());
		var width = reader.ReadInt32();
		var height = reader.ReadInt32();
		var format = (TextureFormat)reader.ReadInt32();
		var length = reader.ReadInt32();
		var texels = reader.ReadBytes(length);

		var layer = new SplashScreenLayer
		{
			Center = center,
			Size = size,
			Width = width,
			Height = height,
			Format = format,
			Texels = texels
		};
		ValidateLayer(layer, expectedFormat);

		return layer;
	}

	private static void ValidateLayer(SplashScreenLayer layer, TextureFormat expectedFormat)
	{
		if (layer.Format != expectedFormat)
		{
			throw new InvalidDataException($"Splash screen layer is {layer.Format}; expected {expectedFormat}.");
		}

		if (layer.Width <= 0 ||
		    layer.Height <= 0 ||
		    layer.Size.X <= 0.0f ||
		    layer.Size.Y <= 0.0f)
		{
			throw new InvalidDataException("Splash screen layer has an empty size.");
		}

		var expectedLength = (long)layer.Width * layer.Height * TextureFormatUtilities.GetBytesPerBlock(layer.Format);
		if (layer.Texels.Length != expectedLength)
		{
			throw new InvalidDataException(
				$"Splash screen layer holds {layer.Texels.Length} bytes; {layer.Width}x{layer.Height} {layer.Format} needs {expectedLength}.");
		}
	}

	private ref struct SpanReader
	{
		private ReadOnlySpan<byte> _remaining;

		public SpanReader(ReadOnlySpan<byte> data)
		{
			_remaining = data;
		}

		public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(sizeof(uint)));

		public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(sizeof(int)));

		public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(sizeof(float)));

		public byte[] ReadBytes(int length)
		{
			if (length < 0)
			{
				throw new InvalidDataException("Splash screen data has a negative length.");
			}

			return Take(length).ToArray();
		}

		private ReadOnlySpan<byte> Take(int length)
		{
			if (_remaining.Length < length)
			{
				throw new InvalidDataException("Splash screen data ends early.");
			}

			var taken = _remaining[..length];
			_remaining = _remaining[length..];

			return taken;
		}
	}
}
