using System.Numerics;

namespace WolfEngine.Rendering;

public struct ColorRGBA
{
	public ColorRGBA(float r, float g, float b, float a)
	{
		R = r;
		G = g;
		B = b;
		A = a;
	}

	public float R { get; set; }
	public float G { get; set; }
	public float B { get; set; }
	public float A { get; set; }

	public Vector4 ToVector4() => new(R, G, B, A);
	public Vector3 ToVector3() => new(R, G, B);

	public static ColorRGBA FromVector4(Vector4 value)
	{
		return new ColorRGBA(r: value.X, g: value.Y, b: value.Z, a: value.W);
	}

	public static ColorRGBA White => new(1.0f, 1.0f, 1.0f, 1.0f);
	public static ColorRGBA Black => new(0.0f, 0.0f, 0.0f, 1.0f);
	public static ColorRGBA Red => new(1.0f, 0.0f, 0.0f, 1.0f);
	public static ColorRGBA Green => new(0.0f, 1.0f, 0.0f, 1.0f);
	public static ColorRGBA Blue => new(0.0f, 0.0f, 1.0f, 1.0f);
	public static ColorRGBA Cyan => new(0.0f, 1.0f, 1.0f, 1.0f);
	public static ColorRGBA Magenta => new(1.0f, 1.0f, 0.0f, 1.0f);
}