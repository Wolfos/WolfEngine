using System.Numerics;
using WolfEngine.Importing;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Projects;

/// <summary>
/// Converts a decoded 2D source into the six faces of a cube texture at import time.
/// </summary>
/// <remarks>
/// <para>
/// Faces are in <see cref="TextureDimension.Cube"/> order (+X, -X, +Y, -Y, +Z, -Z) and laid out the way D3D12 and
/// Metal sample them, so a shader can pass a world direction to GetTextureCube unchanged.
/// </para>
/// <para>
/// An equirectangular source is read with the same direction-to-UV mapping as <c>DirToEnvUV</c> in
/// common_lighting.slang. Sampling the converted cube with a direction therefore returns what the equirectangular
/// sky sampling returns for that direction.
/// </para>
/// </remarks>
internal static class CubemapConverter
{
	private const int MinimumFaceSize = 4;
	private const int MaximumSupersampling = 8;

	public enum SourceLayout
	{
		Equirectangular,
		HorizontalStrip,
		VerticalStrip
	}

	public static ImportedTexture Convert(ImportedTexture source, int requestedFaceSize, int maxFaceSize)
	{
		ArgumentNullException.ThrowIfNull(source.MipLevels);
		if (source.Dimension != TextureDimension.Texture2D || source.MipCount == 0)
		{
			throw new ArgumentException($"Texture '{source.NameOrPath}' is not a 2D source and cannot be converted to a cube.", nameof(source));
		}

		var layout = DetectLayout(source.Width, source.Height)
			?? throw new InvalidOperationException(
				$"Texture '{source.NameOrPath}' is {source.Width}x{source.Height}. A cube texture needs an equirectangular " +
				"source (2:1), or a horizontal (6:1) or vertical (1:6) strip of faces in +X, -X, +Y, -Y, +Z, -Z order.");
		var faceSize = ResolveFaceSize(layout, source.Width, source.Height, requestedFaceSize, maxFaceSize);
		var image = new FloatImage(source.Width, source.Height, ToFloats(source.MipLevels[0].Data, source.Format));
		var faceTexelCount = faceSize * faceSize;
		var faces = new float[faceTexelCount * TextureDescriptor.CubeFaceCount * 4];

		for (var face = 0; face < TextureDescriptor.CubeFaceCount; face++)
		{
			var faceIndex = face;
			Parallel.For(0, faceSize, y =>
			{
				for (var x = 0; x < faceSize; x++)
				{
					var color = layout == SourceLayout.Equirectangular
						? SampleEquirectangular(image, faceIndex, x, y, faceSize)
						: SampleStrip(image, layout, faceIndex, x, y, faceSize);
					var offset = ((faceIndex * faceTexelCount) + (y * faceSize) + x) * 4;
					faces[offset + 0] = color.X;
					faces[offset + 1] = color.Y;
					faces[offset + 2] = color.Z;
					faces[offset + 3] = color.W;
				}
			});
		}

		var data = FromFloats(faces, source.Format);

		return source with
		{
			Width = faceSize,
			Height = faceSize,
			MipLevels = [new TextureMipData(faceSize, faceSize, data, TextureDescriptor.CubeFaceCount)],
			Dimension = TextureDimension.Cube
		};
	}

	public static SourceLayout? DetectLayout(int width, int height)
	{
		if (width <= 0 || height <= 0)
		{
			return null;
		}

		if (width == height * 2)
		{
			return SourceLayout.Equirectangular;
		}

		if (width == height * TextureDescriptor.CubeFaceCount)
		{
			return SourceLayout.HorizontalStrip;
		}

		if (height == width * TextureDescriptor.CubeFaceCount)
		{
			return SourceLayout.VerticalStrip;
		}

		return null;
	}

	/// <summary>
	/// Picks the face size: the requested size, or the source's own face resolution (a quarter of an equirectangular
	/// width, which matches its texel density at the horizon), rounded to a power of two so every mip of a
	/// block-compressed cube stays whole, and capped by <paramref name="maxFaceSize"/>.
	/// </summary>
	public static int ResolveFaceSize(SourceLayout layout, int width, int height, int requestedFaceSize, int maxFaceSize)
	{
		var naturalFaceSize = layout switch
		{
			SourceLayout.Equirectangular => width / 4,
			SourceLayout.HorizontalStrip => height,
			_ => width
		};
		var faceSize = requestedFaceSize > 0
			? requestedFaceSize
			: naturalFaceSize;
		faceSize = RoundToNearestPowerOfTwo(Math.Max(faceSize, MinimumFaceSize));
		if (maxFaceSize > 0)
		{
			faceSize = Math.Min(faceSize, RoundDownToPowerOfTwo(Math.Max(maxFaceSize, MinimumFaceSize)));
		}

		return faceSize;
	}

	/// <summary>
	/// The direction a cube texel faces, using the D3D12 and Metal cube face layout: <paramref name="s"/> runs left to
	/// right and <paramref name="t"/> top to bottom, both in [-1, 1] across the face.
	/// </summary>
	public static Vector3 GetFaceDirection(int face, float s, float t)
	{
		var direction = face switch
		{
			0 => new Vector3(1.0f, -t, -s),
			1 => new Vector3(-1.0f, -t, s),
			2 => new Vector3(s, 1.0f, t),
			3 => new Vector3(s, -1.0f, -t),
			4 => new Vector3(s, -t, 1.0f),
			5 => new Vector3(-s, -t, -1.0f),
			_ => throw new ArgumentOutOfRangeException(nameof(face), face, "A cube has six faces.")
		};

		return Vector3.Normalize(direction);
	}

	/// <summary>The equirectangular UV of a direction, matching <c>DirToEnvUV</c> in common_lighting.slang.</summary>
	public static Vector2 DirectionToEquirectangular(Vector3 direction)
	{
		var u = MathF.Atan2(direction.Z, direction.X) / (2.0f * MathF.PI) + 0.5f;
		var v = MathF.Acos(Math.Clamp(direction.Y, -1.0f, 1.0f)) / MathF.PI;

		return new Vector2(1.0f - u, v);
	}

	private static Vector4 SampleEquirectangular(FloatImage image, int face, int x, int y, int faceSize)
	{
		// A face smaller than the source's own face resolution averages several directions per texel rather than
		// point-sampling the panorama, which would alias.
		var supersampling = Math.Clamp((int)MathF.Ceiling(image.Width / (4.0f * faceSize)), 1, MaximumSupersampling);
		var sum = Vector4.Zero;
		for (var sampleY = 0; sampleY < supersampling; sampleY++)
		{
			for (var sampleX = 0; sampleX < supersampling; sampleX++)
			{
				var s = 2.0f * (x + (sampleX + 0.5f) / supersampling) / faceSize - 1.0f;
				var t = 2.0f * (y + (sampleY + 0.5f) / supersampling) / faceSize - 1.0f;
				var uv = DirectionToEquirectangular(GetFaceDirection(face, s, t));
				sum += image.SampleBilinear(uv.X * image.Width - 0.5f, uv.Y * image.Height - 0.5f, wrapX: true);
			}
		}

		return sum / (supersampling * supersampling);
	}

	private static Vector4 SampleStrip(FloatImage image, SourceLayout layout, int face, int x, int y, int faceSize)
	{
		var sourceFaceSize = layout == SourceLayout.HorizontalStrip
			? image.Height
			: image.Width;
		var scale = sourceFaceSize / (float)faceSize;
		var localX = (x + 0.5f) * scale - 0.5f;
		var localY = (y + 0.5f) * scale - 0.5f;
		var originX = layout == SourceLayout.HorizontalStrip
			? face * sourceFaceSize
			: 0;
		var originY = layout == SourceLayout.VerticalStrip
			? face * sourceFaceSize
			: 0;

		return image.SampleBilinearWithin(localX, localY, originX, originY, sourceFaceSize);
	}

	private static float[] ToFloats(byte[] data, TextureFormat format)
	{
		if (format == TextureFormat.Rgba16Float)
		{
			return HdrPixels.UnpackHalfRgba(data);
		}

		if (format != TextureFormat.Rgba8Unorm)
		{
			throw new NotSupportedException($"Cube conversion does not support {format} sources.");
		}

		var result = new float[data.Length];
		for (var i = 0; i < data.Length; i++)
		{
			result[i] = data[i];
		}

		return result;
	}

	private static byte[] FromFloats(float[] values, TextureFormat format)
	{
		if (format == TextureFormat.Rgba16Float)
		{
			return HdrPixels.PackHalfRgba(values);
		}

		var result = new byte[values.Length];
		for (var i = 0; i < values.Length; i++)
		{
			result[i] = (byte)Math.Clamp(MathF.Round(values[i]), 0.0f, 255.0f);
		}

		return result;
	}

	private static int RoundToNearestPowerOfTwo(int value)
	{
		var lower = RoundDownToPowerOfTwo(value);
		var upper = lower * 2;

		return value - lower < upper - value
			? lower
			: upper;
	}

	private static int RoundDownToPowerOfTwo(int value)
	{
		return 1 << (31 - System.Numerics.BitOperations.LeadingZeroCount((uint)value));
	}

	private sealed class FloatImage(int width, int height, float[] rgba)
	{
		public int Width { get; } = width;

		public int Height { get; } = height;

		/// <summary>Bilinear sample at texel-space coordinates, wrapping or clamping X and always clamping Y.</summary>
		public Vector4 SampleBilinear(float x, float y, bool wrapX)
		{
			var x0 = (int)MathF.Floor(x);
			var y0 = (int)MathF.Floor(y);
			var fx = x - x0;
			var fy = y - y0;
			var left = wrapX ? Wrap(x0, Width) : Math.Clamp(x0, 0, Width - 1);
			var right = wrapX ? Wrap(x0 + 1, Width) : Math.Clamp(x0 + 1, 0, Width - 1);
			var top = Math.Clamp(y0, 0, Height - 1);
			var bottom = Math.Clamp(y0 + 1, 0, Height - 1);

			return Blend(left, right, top, bottom, fx, fy);
		}

		/// <summary>Bilinear sample inside one square region, clamped to its edges so faces do not bleed into each other.</summary>
		public Vector4 SampleBilinearWithin(float x, float y, int originX, int originY, int size)
		{
			var x0 = (int)MathF.Floor(x);
			var y0 = (int)MathF.Floor(y);
			var fx = x - x0;
			var fy = y - y0;
			var left = originX + Math.Clamp(x0, 0, size - 1);
			var right = originX + Math.Clamp(x0 + 1, 0, size - 1);
			var top = originY + Math.Clamp(y0, 0, size - 1);
			var bottom = originY + Math.Clamp(y0 + 1, 0, size - 1);

			return Blend(left, right, top, bottom, fx, fy);
		}

		private Vector4 Blend(int left, int right, int top, int bottom, float fx, float fy)
		{
			var topRow = Vector4.Lerp(Read(left, top), Read(right, top), fx);
			var bottomRow = Vector4.Lerp(Read(left, bottom), Read(right, bottom), fx);

			return Vector4.Lerp(topRow, bottomRow, fy);
		}

		private Vector4 Read(int x, int y)
		{
			var offset = (y * Width + x) * 4;

			return new Vector4(rgba[offset], rgba[offset + 1], rgba[offset + 2], rgba[offset + 3]);
		}

		private static int Wrap(int value, int size)
		{
			var wrapped = value % size;

			return wrapped < 0
				? wrapped + size
				: wrapped;
		}
	}
}
