namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class FixedStepAccumulatorTests
{
	[Test]
	public void Execute_RunsExpectedNumberOfSteps()
	{
		var accumulator = new FixedStepAccumulator(1.0f / 60.0f, 4);
		var steps = 0;

		accumulator.Execute(0.050f, _ => steps++);

		Assert.That(steps, Is.EqualTo(3));
		Assert.That(accumulator.AccumulatedTime, Is.EqualTo(0.0f).Within(0.0001f));
	}

	[Test]
	public void Execute_CapsCatchUpWorkPerFrame()
	{
		var accumulator = new FixedStepAccumulator(1.0f / 60.0f, 4);
		var steps = 0;

		accumulator.Execute(1.0f, _ => steps++);

		Assert.That(steps, Is.EqualTo(4));
		Assert.That(accumulator.AccumulatedTime, Is.EqualTo(0.0f).Within(0.0001f));
	}
}
