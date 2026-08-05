using System;

namespace WolfEngine.Physics;

/// <summary>
/// Holds how far the frame being rendered has advanced past the last completed fixed physics step.
/// </summary>
/// <remarks>
/// The value is published by the host that owns the fixed-step accumulator rather than reconstructed
/// here. Reconstructing it from frame and step deltas is exact only while every assumption about the
/// host's loop holds, and it desynchronizes silently when one does not; taking the accumulator directly
/// cannot drift. A host that never publishes renders every body at its newest simulation sample, which
/// looks exactly like interpolation being switched off.
/// </remarks>
internal sealed class PhysicsInterpolationClock
{
	private float _fixedDeltaTime;
	private float _accumulatedTime;
	private bool _hasAccumulatedTime;

	/// <summary>The most recent fixed timestep the simulation was advanced with.</summary>
	public float FixedDeltaTime => _fixedDeltaTime;

	/// <summary>Time elapsed since the last fixed step, clamped to a single step.</summary>
	public float TimeSinceLastStep => _hasAccumulatedTime
		? Math.Clamp(_accumulatedTime, 0.0f, _fixedDeltaTime)
		: 0.0f;

	/// <summary>
	/// Normalized position between the two most recent fixed steps, in the range [0, 1]. Defaults to the
	/// newest sample until the host publishes its accumulator.
	/// </summary>
	public float Alpha => _hasAccumulatedTime && _fixedDeltaTime > 0.0f
		? Math.Clamp(_accumulatedTime / _fixedDeltaTime, 0.0f, 1.0f)
		: 1.0f;

	public void OnFixedStep(float fixedDeltaTime)
	{
		if (fixedDeltaTime > 0.0f)
		{
			_fixedDeltaTime = fixedDeltaTime;
		}
	}

	public void PublishAccumulatedTime(float accumulatedTime, float fixedDeltaTime)
	{
		if (fixedDeltaTime > 0.0f)
		{
			_fixedDeltaTime = fixedDeltaTime;
		}

		_accumulatedTime = MathF.Max(accumulatedTime, 0.0f);
		_hasAccumulatedTime = true;
	}
}
