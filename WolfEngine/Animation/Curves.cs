using System.Numerics;

namespace WolfEngine.Animation;

public enum CurveInterpolation
{
	Constant = 0,
	Linear = 1,
	CubicHermite = 2
}

/// <summary>
/// Locates the keyframe segment containing a time value. Sampling takes a caller-owned cursor so
/// sequential playback costs one comparison per frame instead of a binary search; the cursor is
/// only a hint, so a stale or wrong value costs performance and never correctness.
/// </summary>
internal static class CurveKeys
{
	/// <summary>
	/// Resolves <paramref name="time"/> to a segment. Returns false when the curve has no keys.
	/// On return, the sample is lerp(values[keyA], values[keyB], blend).
	/// </summary>
	internal static bool TryFindSegment(
		float[] times,
		float time,
		ref int cursor,
		out int keyA,
		out int keyB,
		out float blend)
	{
		keyA = 0;
		keyB = 0;
		blend = 0.0f;

		if (times is null || times.Length == 0)
		{
			return false;
		}

		if (times.Length == 1 || time <= times[0])
		{
			cursor = 0;
			return true;
		}

		var lastIndex = times.Length - 1;
		if (time >= times[lastIndex])
		{
			cursor = lastIndex;
			keyA = lastIndex;
			keyB = lastIndex;
			return true;
		}

		// Playback is overwhelmingly sequential, so try the cached segment and its successor first.
		var index = cursor;
		if (index < 0 || index >= lastIndex || time < times[index])
		{
			index = BinarySearchSegment(times, time);
		}
		else if (time >= times[index + 1])
		{
			if (index + 2 <= lastIndex && time < times[index + 2])
			{
				index += 1;
			}
			else
			{
				index = BinarySearchSegment(times, time);
			}
		}

		cursor = index;
		keyA = index;
		keyB = index + 1;

		var segmentDuration = times[keyB] - times[keyA];
		blend = segmentDuration > 0.0f ? (time - times[keyA]) / segmentDuration : 0.0f;
		return true;
	}

	/// <summary>Returns the index of the last key at or before <paramref name="time"/>.</summary>
	private static int BinarySearchSegment(float[] times, float time)
	{
		var low = 0;
		var high = times.Length - 1;
		while (low < high)
		{
			var mid = (low + high + 1) / 2;
			if (times[mid] <= time)
			{
				low = mid;
			}
			else
			{
				high = mid - 1;
			}
		}

		return low;
	}

	internal static float SegmentDuration(float[] times, int keyA, int keyB) =>
		keyA == keyB ? 0.0f : times[keyB] - times[keyA];
}

/// <summary>
/// Scalar keyframe curve. Used by property tracks today; the curve editor will author these
/// directly, which is why <see cref="CurveInterpolation.CubicHermite"/> and the tangent arrays
/// exist in the format from the start.
/// </summary>
public sealed class FloatCurve
{
	public static readonly FloatCurve Empty = new(Array.Empty<float>(), Array.Empty<float>());

	public FloatCurve(
		float[] times,
		float[] values,
		CurveInterpolation interpolation = CurveInterpolation.Linear,
		float[]? inTangents = null,
		float[]? outTangents = null)
	{
		Times = times ?? throw new ArgumentNullException(nameof(times));
		Values = values ?? throw new ArgumentNullException(nameof(values));
		if (Times.Length != Values.Length)
		{
			throw new ArgumentException("Curve key and value counts must match.", nameof(values));
		}

		Interpolation = interpolation;
		InTangents = inTangents;
		OutTangents = outTangents;
	}

	public float[] Times { get; }
	public float[] Values { get; }
	public CurveInterpolation Interpolation { get; }
	public float[]? InTangents { get; }
	public float[]? OutTangents { get; }

	public bool IsEmpty => Times.Length == 0;

	public float Evaluate(float time, ref int cursor, float defaultValue = 0.0f)
	{
		if (CurveKeys.TryFindSegment(Times, time, ref cursor, out var keyA, out var keyB, out var blend) == false)
		{
			return defaultValue;
		}

		if (keyA == keyB || Interpolation == CurveInterpolation.Constant)
		{
			return Values[keyA];
		}

		if (Interpolation == CurveInterpolation.CubicHermite && InTangents is not null && OutTangents is not null)
		{
			return HermiteInterpolation.Evaluate(
				Values[keyA],
				OutTangents[keyA],
				Values[keyB],
				InTangents[keyB],
				CurveKeys.SegmentDuration(Times, keyA, keyB),
				blend);
		}

		return float.Lerp(Values[keyA], Values[keyB], blend);
	}
}

/// <summary>Vector keyframe curve, used for bone translation and scale.</summary>
public sealed class Vector3Curve
{
	public static readonly Vector3Curve Empty = new(Array.Empty<float>(), Array.Empty<Vector3>());

	public Vector3Curve(
		float[] times,
		Vector3[] values,
		CurveInterpolation interpolation = CurveInterpolation.Linear,
		Vector3[]? inTangents = null,
		Vector3[]? outTangents = null)
	{
		Times = times ?? throw new ArgumentNullException(nameof(times));
		Values = values ?? throw new ArgumentNullException(nameof(values));
		if (Times.Length != Values.Length)
		{
			throw new ArgumentException("Curve key and value counts must match.", nameof(values));
		}

		Interpolation = interpolation;
		InTangents = inTangents;
		OutTangents = outTangents;
	}

	public float[] Times { get; }
	public Vector3[] Values { get; }
	public CurveInterpolation Interpolation { get; }
	public Vector3[]? InTangents { get; }
	public Vector3[]? OutTangents { get; }

	public bool IsEmpty => Times.Length == 0;

	public Vector3 Evaluate(float time, ref int cursor, Vector3 defaultValue)
	{
		if (CurveKeys.TryFindSegment(Times, time, ref cursor, out var keyA, out var keyB, out var blend) == false)
		{
			return defaultValue;
		}

		if (keyA == keyB || Interpolation == CurveInterpolation.Constant)
		{
			return Values[keyA];
		}

		if (Interpolation == CurveInterpolation.CubicHermite && InTangents is not null && OutTangents is not null)
		{
			return HermiteInterpolation.Evaluate(
				Values[keyA],
				OutTangents[keyA],
				Values[keyB],
				InTangents[keyB],
				CurveKeys.SegmentDuration(Times, keyA, keyB),
				blend);
		}

		return Vector3.Lerp(Values[keyA], Values[keyB], blend);
	}
}

/// <summary>Rotation keyframe curve. Interpolates with shortest-path nlerp.</summary>
public sealed class QuaternionCurve
{
	public static readonly QuaternionCurve Empty = new(Array.Empty<float>(), Array.Empty<Quaternion>());

	public QuaternionCurve(
		float[] times,
		Quaternion[] values,
		CurveInterpolation interpolation = CurveInterpolation.Linear)
	{
		Times = times ?? throw new ArgumentNullException(nameof(times));
		Values = values ?? throw new ArgumentNullException(nameof(values));
		if (Times.Length != Values.Length)
		{
			throw new ArgumentException("Curve key and value counts must match.", nameof(values));
		}

		Interpolation = interpolation;
	}

	public float[] Times { get; }
	public Quaternion[] Values { get; }
	public CurveInterpolation Interpolation { get; }

	public bool IsEmpty => Times.Length == 0;

	public Quaternion Evaluate(float time, ref int cursor, Quaternion defaultValue)
	{
		if (CurveKeys.TryFindSegment(Times, time, ref cursor, out var keyA, out var keyB, out var blend) == false)
		{
			return defaultValue;
		}

		if (keyA == keyB || Interpolation == CurveInterpolation.Constant)
		{
			return Values[keyA];
		}

		return QuaternionMath.Nlerp(Values[keyA], Values[keyB], blend);
	}
}

internal static class HermiteInterpolation
{
	/// <summary>
	/// Cubic Hermite over a segment. Tangents are in value-per-second, so they are scaled by the
	/// segment duration; that is the same convention glTF CUBICSPLINE uses.
	/// </summary>
	internal static float Evaluate(float valueA, float outTangentA, float valueB, float inTangentB, float duration, float t)
	{
		var (h00, h10, h01, h11) = Basis(t);
		return (h00 * valueA) + (h10 * duration * outTangentA) + (h01 * valueB) + (h11 * duration * inTangentB);
	}

	internal static Vector3 Evaluate(Vector3 valueA, Vector3 outTangentA, Vector3 valueB, Vector3 inTangentB, float duration, float t)
	{
		var (h00, h10, h01, h11) = Basis(t);
		return (h00 * valueA) + (h10 * duration * outTangentA) + (h01 * valueB) + (h11 * duration * inTangentB);
	}

	private static (float H00, float H10, float H01, float H11) Basis(float t)
	{
		var t2 = t * t;
		var t3 = t2 * t;
		return (
			(2.0f * t3) - (3.0f * t2) + 1.0f,
			t3 - (2.0f * t2) + t,
			(-2.0f * t3) + (3.0f * t2),
			t3 - t2);
	}
}

public static class QuaternionMath
{
	/// <summary>
	/// Normalized lerp along the shortest arc. Cheaper than slerp and indistinguishable at the
	/// key densities animation data actually ships with, but the sign flip is not optional:
	/// without it a rotation crossing the antipode spins the long way round.
	/// </summary>
	public static Quaternion Nlerp(Quaternion a, Quaternion b, float t)
	{
		if (Quaternion.Dot(a, b) < 0.0f)
		{
			b = new Quaternion(-b.X, -b.Y, -b.Z, -b.W);
		}

		var result = new Quaternion(
			float.Lerp(a.X, b.X, t),
			float.Lerp(a.Y, b.Y, t),
			float.Lerp(a.Z, b.Z, t),
			float.Lerp(a.W, b.W, t));

		var lengthSquared = result.LengthSquared();
		return lengthSquared > 0.0f ? Quaternion.Normalize(result) : Quaternion.Identity;
	}
}
