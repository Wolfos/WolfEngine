using System.Numerics;
using WolfEngine.Mathematics;

namespace WolfEngine.Rendering;

public static class TemporalJitter
{
	public const int DefaultPhaseCount = 8;

	public static Vector2 GetHaltonJitterPixels(ulong frameIndex, int phaseCount = DefaultPhaseCount)
	{
		var phase = phaseCount <= 0 ? DefaultPhaseCount : phaseCount;
		var sampleIndex = (int)(frameIndex % (ulong)phase) + 1;
		return new Vector2(
			Halton(sampleIndex, 2) - 0.5f,
			Halton(sampleIndex, 3) - 0.5f);
	}

	public static Vector2 GetJitterNdc(Vector2 jitterPixels, Int2 renderSize)
	{
		if (renderSize.X <= 0 || renderSize.Y <= 0)
		{
			return Vector2.Zero;
		}

		return new Vector2(
			(2.0f * jitterPixels.X) / renderSize.X,
			(-2.0f * jitterPixels.Y) / renderSize.Y);
	}

	public static Matrix4x4 ApplyProjectionJitter(in Matrix4x4 projection, Vector2 jitterNdc)
	{
		var jittered = projection;
		jittered.M31 += jitterNdc.X;
		jittered.M32 += jitterNdc.Y;
		return jittered;
	}

	internal static bool HasProjectionChanged(
		in Matrix4x4 current,
		in Matrix4x4 previous,
		float epsilon = 1e-5f)
	{
		var threshold = MathF.Max(epsilon, 0.0f);
		return MathF.Abs(current.M11 - previous.M11) > threshold ||
		       MathF.Abs(current.M12 - previous.M12) > threshold ||
		       MathF.Abs(current.M13 - previous.M13) > threshold ||
		       MathF.Abs(current.M14 - previous.M14) > threshold ||
		       MathF.Abs(current.M21 - previous.M21) > threshold ||
		       MathF.Abs(current.M22 - previous.M22) > threshold ||
		       MathF.Abs(current.M23 - previous.M23) > threshold ||
		       MathF.Abs(current.M24 - previous.M24) > threshold ||
		       MathF.Abs(current.M31 - previous.M31) > threshold ||
		       MathF.Abs(current.M32 - previous.M32) > threshold ||
		       MathF.Abs(current.M33 - previous.M33) > threshold ||
		       MathF.Abs(current.M34 - previous.M34) > threshold ||
		       MathF.Abs(current.M41 - previous.M41) > threshold ||
		       MathF.Abs(current.M42 - previous.M42) > threshold ||
		       MathF.Abs(current.M43 - previous.M43) > threshold ||
		       MathF.Abs(current.M44 - previous.M44) > threshold;
	}

	private static float Halton(int index, int @base)
	{
		var fraction = 1.0f;
		var result = 0.0f;
		var value = index;
		while (value > 0)
		{
			fraction /= @base;
			result += fraction * (value % @base);
			value /= @base;
		}

		return result;
	}
}
