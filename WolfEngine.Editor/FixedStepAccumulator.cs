using System;

namespace WolfEngine.Editor;

internal sealed class FixedStepAccumulator
{
	private readonly float _fixedDeltaTime;
	private readonly int _maxStepsPerFrame;
	private readonly float _maxAccumulatedTime;
	private float _accumulatedTime;

	public FixedStepAccumulator(float fixedDeltaTime, int maxStepsPerFrame)
	{
		if (fixedDeltaTime <= 0.0f)
		{
			throw new ArgumentOutOfRangeException(nameof(fixedDeltaTime));
		}

		if (maxStepsPerFrame <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(maxStepsPerFrame));
		}

		_fixedDeltaTime = fixedDeltaTime;
		_maxStepsPerFrame = maxStepsPerFrame;
		_maxAccumulatedTime = fixedDeltaTime * maxStepsPerFrame;
	}

	internal float AccumulatedTime => _accumulatedTime;

	public int Execute(float frameDeltaTime, Action<float> stepAction)
	{
		ArgumentNullException.ThrowIfNull(stepAction);
		if (frameDeltaTime <= 0.0f)
		{
			return 0;
		}

		_accumulatedTime = MathF.Min(_accumulatedTime + frameDeltaTime, _maxAccumulatedTime);

		var executedSteps = 0;
		while (executedSteps < _maxStepsPerFrame && _accumulatedTime + 0.000001f >= _fixedDeltaTime)
		{
			_accumulatedTime -= _fixedDeltaTime;
			if (_accumulatedTime < 0.0f)
			{
				_accumulatedTime = 0.0f;
			}

			stepAction(_fixedDeltaTime);
			executedSteps++;
		}

		return executedSteps;
	}

	public void Reset()
	{
		_accumulatedTime = 0.0f;
	}
}
