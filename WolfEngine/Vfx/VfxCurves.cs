using System.Numerics;
using System.Text.Json.Serialization;
using WolfEngine.Rendering;

namespace WolfEngine.Vfx;

/// <summary>A random value picked uniformly between <see cref="Min"/> and <see cref="Max"/> for each particle.</summary>
public struct VfxFloatRange
{
	public VfxFloatRange(float min, float max)
	{
		Min = min;
		Max = max;
	}

	public VfxFloatRange(float value)
		: this(value, value)
	{
	}

	public float Min { get; set; }
	public float Max { get; set; }

	public readonly float Largest => MathF.Max(Min, Max);

	public readonly Vector2 ToVector2() => new(Min, Max);
}

public struct VfxCurveKey
{
	public VfxCurveKey(float time, float value)
	{
		Time = time;
		Value = value;
	}

	/// <summary>Normalised time, 0 at the start of a particle's life (or the head of a ribbon) and 1 at the end.</summary>
	public float Time { get; set; }

	public float Value { get; set; }
}

/// <summary>
/// A piecewise linear curve over normalised time. VFX curves are deliberately simple: they are baked into a fixed
/// number of samples for the GPU, so finer control than linear segments would not survive the bake anyway.
/// </summary>
public sealed class VfxCurve
{
	/// <summary>Samples each curve is baked into on the GPU. Shared with <c>vfx_common.slang</c>.</summary>
	public const int BakedSampleCount = 16;

	public VfxCurve()
	{
	}

	public VfxCurve(params VfxCurveKey[] keys)
	{
		Keys = new List<VfxCurveKey>(keys);
	}

	[JsonInclude]
	public List<VfxCurveKey> Keys { get; set; } =
	[
		new(0.0f, 1.0f),
		new(1.0f, 1.0f)
	];

	public static VfxCurve Constant(float value) => new(new VfxCurveKey(0.0f, value), new VfxCurveKey(1.0f, value));

	public float Evaluate(float time)
	{
		if (Keys.Count == 0)
		{
			return 1.0f;
		}

		if (time <= Keys[0].Time || Keys.Count == 1)
		{
			return Keys[0].Value;
		}

		for (var i = 1; i < Keys.Count; i++)
		{
			var next = Keys[i];
			if (time > next.Time)
			{
				continue;
			}

			var previous = Keys[i - 1];
			var span = next.Time - previous.Time;
			var blend = span > 0.0f ? (time - previous.Time) / span : 1.0f;

			return previous.Value + (next.Value - previous.Value) * blend;
		}

		return Keys[^1].Value;
	}

	public void Bake(Span<float> destination)
	{
		for (var i = 0; i < destination.Length; i++)
		{
			destination[i] = Evaluate(i / (float)Math.Max(destination.Length - 1, 1));
		}
	}

	/// <summary>Keeps keys ordered by time, which <see cref="Evaluate"/> relies on. Editors call this after a change.</summary>
	public void Sort() => Keys.Sort(static (a, b) => a.Time.CompareTo(b.Time));
}

public struct VfxGradientKey
{
	public VfxGradientKey(float time, ColorRGBA color)
	{
		Time = time;
		Color = color;
	}

	public float Time { get; set; }

	/// <summary>Linear colour; alpha is opacity.</summary>
	public ColorRGBA Color { get; set; }
}

/// <summary>A piecewise linear colour and opacity ramp over normalised time, baked like <see cref="VfxCurve"/>.</summary>
public sealed class VfxGradient
{
	public VfxGradient()
	{
	}

	public VfxGradient(params VfxGradientKey[] keys)
	{
		Keys = new List<VfxGradientKey>(keys);
	}

	[JsonInclude]
	public List<VfxGradientKey> Keys { get; set; } =
	[
		new(0.0f, ColorRGBA.White),
		new(1.0f, new ColorRGBA(1.0f, 1.0f, 1.0f, 0.0f))
	];

	public Vector4 Evaluate(float time)
	{
		if (Keys.Count == 0)
		{
			return Vector4.One;
		}

		if (time <= Keys[0].Time || Keys.Count == 1)
		{
			return Keys[0].Color;
		}

		for (var i = 1; i < Keys.Count; i++)
		{
			var next = Keys[i];
			if (time > next.Time)
			{
				continue;
			}

			var previous = Keys[i - 1];
			var span = next.Time - previous.Time;
			var blend = span > 0.0f ? (time - previous.Time) / span : 1.0f;

			return Vector4.Lerp(previous.Color, next.Color, blend);
		}

		return Keys[^1].Color;
	}

	public void Bake(Span<Vector4> destination)
	{
		for (var i = 0; i < destination.Length; i++)
		{
			destination[i] = Evaluate(i / (float)Math.Max(destination.Length - 1, 1));
		}
	}

	public void Sort() => Keys.Sort(static (a, b) => a.Time.CompareTo(b.Time));
}
