using System;

namespace WolfEngine.Physics;

/// <summary>Tracks render time since the last fixed physics step.</summary>
internal sealed class PhysicsInterpolationClock
{
	private const float DefaultFixedDeltaTime = 1.0f / 60.0f;

	private const int MaxTrailingSteps = 16;

	private float _fixedDeltaTime = DefaultFixedDeltaTime;
	private float _timeSinceLastStep;

	public float FixedDeltaTime => _fixedDeltaTime;

	public float TimeSinceLastStep => Math.Clamp(_timeSinceLastStep, 0.0f, _fixedDeltaTime);

	public float Alpha => _fixedDeltaTime > 0.0f ? Math.Clamp(_timeSinceLastStep / _fixedDeltaTime, 0.0f, 1.0f) : 0.0f;

	public void OnFixedStep(float fixedDeltaTime)
	{
		if (fixedDeltaTime <= 0.0f)
		{
			return;
		}

		_fixedDeltaTime = fixedDeltaTime;
		_timeSinceLastStep = MathF.Max(_timeSinceLastStep - fixedDeltaTime, -fixedDeltaTime * MaxTrailingSteps);
	}

	public void OnFrame(float deltaTime)
	{
		if (deltaTime <= 0.0f)
		{
			return;
		}

		_timeSinceLastStep = MathF.Min(_timeSinceLastStep + deltaTime, _fixedDeltaTime);
	}

	public void Reset()
	{
		_timeSinceLastStep = 0.0f;
	}
}
