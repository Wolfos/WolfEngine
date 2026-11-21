using System.Runtime.InteropServices;

namespace WolfEngine.Importing;

/// <summary>
/// Conversions for HDR texture content, which the import pipeline keeps as linear RGBA half floats
/// (<see cref="Rendering.TextureFormat.Rgba16Float"/>, eight bytes per pixel).
/// </summary>
public static class HdrPixels
{
	public const int BytesPerPixel = 8;

	/// <summary>Largest finite half float. BC6H cannot store anything brighter either.</summary>
	public const float MaxValue = 65504.0f;

	/// <summary>
	/// Packs linear float RGBA into half floats. Values are clamped to [0, <see cref="MaxValue"/>] and NaN becomes
	/// zero: an unsigned HDR texture has no use for negative or non-finite texels, and BC6H's unsigned mode cannot
	/// encode them.
	/// </summary>
	public static byte[] PackHalfRgba(ReadOnlySpan<float> rgba)
	{
		if (rgba.Length % 4 != 0)
		{
			throw new ArgumentException("RGBA data must hold four floats per pixel.", nameof(rgba));
		}

		var result = new byte[rgba.Length * 2];
		var halves = MemoryMarshal.Cast<byte, Half>(result.AsSpan());
		for (var i = 0; i < rgba.Length; i++)
		{
			halves[i] = (Half)Sanitize(rgba[i]);
		}

		return result;
	}

	/// <summary>Unpacks half-float RGBA into floats.</summary>
	public static float[] UnpackHalfRgba(ReadOnlySpan<byte> halfRgba)
	{
		if (halfRgba.Length % BytesPerPixel != 0)
		{
			throw new ArgumentException("Half-float RGBA data must hold eight bytes per pixel.", nameof(halfRgba));
		}

		var halves = MemoryMarshal.Cast<byte, Half>(halfRgba);
		var result = new float[halves.Length];
		for (var i = 0; i < halves.Length; i++)
		{
			result[i] = (float)halves[i];
		}

		return result;
	}

	/// <summary>
	/// Converts half-float RGBA to 8-bit sRGB-encoded RGBA for previews. Values above one are clipped: a preview
	/// shows the image at its stored exposure rather than tone mapping it.
	/// </summary>
	public static byte[] ToPreviewRgba8Srgb(ReadOnlySpan<byte> halfRgba)
	{
		if (halfRgba.Length % BytesPerPixel != 0)
		{
			throw new ArgumentException("Half-float RGBA data must hold eight bytes per pixel.", nameof(halfRgba));
		}

		var halves = MemoryMarshal.Cast<byte, Half>(halfRgba);
		var result = new byte[halves.Length];
		for (var i = 0; i < halves.Length; i++)
		{
			var value = Math.Clamp(Sanitize((float)halves[i]), 0.0f, 1.0f);
			var isAlpha = i % 4 == 3;
			var encoded = isAlpha
				? value
				: LinearToSrgb(value);
			result[i] = (byte)MathF.Round(encoded * 255.0f);
		}

		return result;
	}

	/// <summary>Whether <paramref name="data"/> is a Radiance RGBE (.hdr) file, by the same header test stb_image uses.</summary>
	public static bool IsRadianceHdr(ReadOnlySpan<byte> data)
	{
		return data.StartsWith("#?RADIANCE\n"u8) ||
		       data.StartsWith("#?RGBE\n"u8);
	}

	private static float Sanitize(float value)
	{
		if (float.IsNaN(value))
		{
			return 0.0f;
		}

		return Math.Clamp(value, 0.0f, MaxValue);
	}

	private static float LinearToSrgb(float value)
	{
		return value <= 0.0031308f
			? value * 12.92f
			: 1.055f * MathF.Pow(value, 1.0f / 2.4f) - 0.055f;
	}
}
