using System.Numerics;
using WolfEngine.Mathematics;

namespace WolfEngine.Rendering;

public static class TemporalJitter
{
	public const int DefaultPhaseCount = 16;

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
