using System.Numerics;
using WolfEngine.Animation;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class AnimationSamplingTests
{
	[Test]
	public void FloatCurve_InterpolatesLinearlyAndClampsOutsideKeyRange()
	{
		var curve = new FloatCurve([0.0f, 1.0f, 3.0f], [10.0f, 20.0f, 0.0f]);
		var cursor = 0;

		Assert.Multiple(() =>
		{
			Assert.That(curve.Evaluate(-5.0f, ref cursor), Is.EqualTo(10.0f).Within(1e-5f), "before first key");
			Assert.That(curve.Evaluate(0.5f, ref cursor), Is.EqualTo(15.0f).Within(1e-5f));
			Assert.That(curve.Evaluate(2.0f, ref cursor), Is.EqualTo(10.0f).Within(1e-5f), "mid second segment");
			Assert.That(curve.Evaluate(99.0f, ref cursor), Is.EqualTo(0.0f).Within(1e-5f), "after last key");
		});
	}

	[Test]
	public void FloatCurve_ConstantInterpolationHoldsTheEarlierKey()
	{
		var curve = new FloatCurve([0.0f, 1.0f], [10.0f, 20.0f], CurveInterpolation.Constant);
		var cursor = 0;
		Assert.That(curve.Evaluate(0.99f, ref cursor), Is.EqualTo(10.0f).Within(1e-5f));
	}

	[Test]
	public void FloatCurve_EmptyCurveReturnsTheSuppliedDefault()
	{
		var cursor = 0;
		Assert.That(FloatCurve.Empty.Evaluate(1.0f, ref cursor, defaultValue: 7.5f), Is.EqualTo(7.5f));
	}

	/// <summary>
	/// A stale or wrong cursor is only ever a hint, so sampling out of order has to agree with
	/// sampling in order. Playback and editor scrubbing share the same curve objects.
	/// </summary>
	[Test]
	public void FloatCurve_RandomAccessMatchesSequentialAccess()
	{
		var times = new float[32];
		var values = new float[32];
		for (var i = 0; i < times.Length; i++)
		{
			times[i] = i * 0.25f;
			values[i] = MathF.Sin(i);
		}

		var curve = new FloatCurve(times, values);

		var sequentialCursor = 0;
		var sequential = new float[64];
		for (var i = 0; i < sequential.Length; i++)
		{
			sequential[i] = curve.Evaluate(i * 0.125f, ref sequentialCursor);
		}

		for (var i = sequential.Length - 1; i >= 0; i--)
		{
			var scrubCursor = 31;
			Assert.That(curve.Evaluate(i * 0.125f, ref scrubCursor), Is.EqualTo(sequential[i]).Within(1e-6f), $"sample {i}");
		}
	}

	[Test]
	public void FloatCurve_CubicHermiteMatchesEndpointsAndTangents()
	{
		var curve = new FloatCurve(
			[0.0f, 2.0f],
			[0.0f, 1.0f],
			CurveInterpolation.CubicHermite,
			inTangents: [0.0f, 0.0f],
			outTangents: [0.0f, 0.0f]);
		var cursor = 0;

		Assert.Multiple(() =>
		{
			Assert.That(curve.Evaluate(0.0f, ref cursor), Is.EqualTo(0.0f).Within(1e-5f));
			Assert.That(curve.Evaluate(2.0f, ref cursor), Is.EqualTo(1.0f).Within(1e-5f));
			// Flat tangents make the midpoint the smoothstep value rather than the linear one.
			Assert.That(curve.Evaluate(1.0f, ref cursor), Is.EqualTo(0.5f).Within(1e-5f));
		});
	}

	/// <summary>
	/// Without the sign flip, a rotation whose keys straddle the antipode interpolates the long way
	/// round — a limb visibly swinging through the body rather than to the next pose.
	/// </summary>
	[Test]
	public void QuaternionCurve_TakesTheShortestArcAcrossTheAntipode()
	{
		var start = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.1f);
		var end = new Quaternion(-start.X, -start.Y, -start.Z, -start.W);
		var curve = new QuaternionCurve([0.0f, 1.0f], [start, end]);
		var cursor = 0;

		var middle = curve.Evaluate(0.5f, ref cursor, Quaternion.Identity);

		Assert.That(MathF.Abs(Quaternion.Dot(middle, start)), Is.EqualTo(1.0f).Within(1e-4f),
			"interpolating between a rotation and its negation must not move away from that rotation");
	}

	[Test]
	public void QuaternionCurve_ReturnsNormalizedRotations()
	{
		var curve = new QuaternionCurve(
			[0.0f, 1.0f],
			[
				Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.0f),
				Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI * 0.5f)
			]);
		var cursor = 0;

		Assert.That(curve.Evaluate(0.5f, ref cursor, Quaternion.Identity).Length(), Is.EqualTo(1.0f).Within(1e-5f));
	}

	[TestCase(-1.0f, 3.0f)]
	[TestCase(0.5f, 0.5f)]
	[TestCase(4.5f, 0.5f)]
	public void AnimationClip_LoopingTimeWrapsIntoRange(float input, float expected)
	{
		var clip = CreateClip(duration: 4.0f, loop: true);
		Assert.That(clip.NormalizeTime(input), Is.EqualTo(expected).Within(1e-5f));
	}

	[TestCase(-1.0f, 0.0f)]
	[TestCase(9.0f, 4.0f)]
	public void AnimationClip_NonLoopingTimeClampsToRange(float input, float expected)
	{
		var clip = CreateClip(duration: 4.0f, loop: false);
		Assert.That(clip.NormalizeTime(input), Is.EqualTo(expected).Within(1e-5f));
	}

	private static AnimationClip CreateClip(float duration, bool loop) =>
		new("clip", duration, 30.0f, loop, [], [], string.Empty, []);
}
