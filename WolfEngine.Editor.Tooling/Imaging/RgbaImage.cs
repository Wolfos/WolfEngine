using StbImageSharp;
using StbImageWriteSharp;
using ReadComponents = StbImageSharp.ColorComponents;
using WriteComponents = StbImageWriteSharp.ColorComponents;

namespace WolfEngine.Editor.Tooling.Imaging;

public readonly record struct RgbaPixel(byte R, byte G, byte B, byte A = 255);

/// <summary>8-bit RGBA pixels in row-major order, decoded and encoded with the public-domain stb libraries.</summary>
public sealed class RgbaImage
{
	public RgbaImage(int width, int height, byte[] pixels)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
		if (pixels.Length != width * height * 4)
		{
			throw new ArgumentException($"Expected {width * height * 4} bytes for a {width}x{height} RGBA image, got {pixels.Length}.", nameof(pixels));
		}

		Width = width;
		Height = height;
		Pixels = pixels;
	}

	public RgbaImage(int width, int height, RgbaPixel fill = default)
		: this(width, height, new byte[width * height * 4])
	{
		if (fill != default)
		{
			for (var offset = 0; offset < Pixels.Length; offset += 4)
			{
				Pixels[offset] = fill.R;
				Pixels[offset + 1] = fill.G;
				Pixels[offset + 2] = fill.B;
				Pixels[offset + 3] = fill.A;
			}
		}
	}

	public int Width { get; }
	public int Height { get; }
	public byte[] Pixels { get; }

	public RgbaPixel this[int x, int y]
	{
		get
		{
			var offset = Offset(x, y);
			return new RgbaPixel(Pixels[offset], Pixels[offset + 1], Pixels[offset + 2], Pixels[offset + 3]);
		}
		set
		{
			var offset = Offset(x, y);
			Pixels[offset] = value.R;
			Pixels[offset + 1] = value.G;
			Pixels[offset + 2] = value.B;
			Pixels[offset + 3] = value.A;
		}
	}

	/// <summary>Decodes any format stb_image reads (png, jpg, bmp, tga, psd, gif, hdr, pic, pnm), expanded to RGBA.</summary>
	public static RgbaImage Load(string path)
	{
		var image = ImageResult.FromMemory(File.ReadAllBytes(path), ReadComponents.RedGreenBlueAlpha);
		return new RgbaImage(image.Width, image.Height, image.Data);
	}

	/// <summary>Reads only the header.</summary>
	public static (int Width, int Height) Identify(string path)
	{
		using var stream = File.OpenRead(path);
		var info = ImageInfo.FromStream(stream)
			?? throw new InvalidDataException($"'{path}' is not an image format stb_image can read.");
		return (info.Width, info.Height);
	}

	public void SavePng(string path)
	{
		using var stream = File.Create(path);
		new ImageWriter().WritePng(Pixels, Width, Height, WriteComponents.RedGreenBlueAlpha, stream);
	}

	public RgbaImage Clone() => new(Width, Height, (byte[])Pixels.Clone());

	/// <summary>Bilinear resample with pixel centers aligned; meant for matching map sizes, not heavy downscaling.</summary>
	public RgbaImage Resize(int width, int height)
	{
		if (width == Width && height == Height)
		{
			return Clone();
		}

		var output = new RgbaImage(width, height);
		var scaleX = Width / (float)width;
		var scaleY = Height / (float)height;
		for (var y = 0; y < height; y++)
		{
			var sy = Math.Clamp((y + 0.5f) * scaleY - 0.5f, 0, Height - 1);
			var y0 = (int)sy;
			var y1 = Math.Min(y0 + 1, Height - 1);
			var ty = sy - y0;
			for (var x = 0; x < width; x++)
			{
				var sx = Math.Clamp((x + 0.5f) * scaleX - 0.5f, 0, Width - 1);
				var x0 = (int)sx;
				var x1 = Math.Min(x0 + 1, Width - 1);
				var tx = sx - x0;
				var destination = output.Offset(x, y);
				for (var channel = 0; channel < 4; channel++)
				{
					var top = Lerp(Pixels[Offset(x0, y0) + channel], Pixels[Offset(x1, y0) + channel], tx);
					var bottom = Lerp(Pixels[Offset(x0, y1) + channel], Pixels[Offset(x1, y1) + channel], tx);
					output.Pixels[destination + channel] = (byte)MathF.Round(Lerp(top, bottom, ty));
				}
			}
		}

		return output;
	}

	private int Offset(int x, int y)
	{
		if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
		{
			throw new ArgumentOutOfRangeException(nameof(x), $"Pixel ({x}, {y}) is outside the {Width}x{Height} image.");
		}

		return (y * Width + x) * 4;
	}

	private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
