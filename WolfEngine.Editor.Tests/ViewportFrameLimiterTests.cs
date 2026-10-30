namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class ViewportFrameLimiterTests
{
	private static readonly TimeSpan DisplayInterval = TimeSpan.FromSeconds(1.0 / 60.0);

	[Test]
	public void WithoutALimitEveryFrameDraws()
	{
		var limiter = new ViewportFrameLimiter();

		for (var frame = 0; frame < 10; frame++)
		{
			Assert.That(limiter.TryBeginFrame(DisplayInterval * frame, 0), Is.True);
		}
	}

	[Test]
	public void HalfTheDisplayRateDrawsEveryOtherFrameDespiteJitter()
	{
		// A 60 Hz display delivers frames a little early or late. Without a tolerance, a frame landing just
		// before the exact due time is skipped and 30 FPS becomes 20.
		var limiter = new ViewportFrameLimiter();
		var jitter = TimeSpan.FromMilliseconds(1.0);
		var drawn = new List<int>();
		for (var frame = 0; frame < 12; frame++)
		{
			var now = DisplayInterval * frame + (frame % 2 == 0 ? -jitter : jitter);
			if (limiter.TryBeginFrame(now, 30))
			{
				drawn.Add(frame);
			}
		}

		Assert.That(drawn, Is.EqualTo(new[] { 0, 2, 4, 6, 8, 10 }));
	}

	[Test]
	public void AStallRestartsTheCadenceInsteadOfBursting()
	{
		var limiter = new ViewportFrameLimiter();
		var interval = TimeSpan.FromSeconds(1.0 / 20.0);
		Assert.That(limiter.TryBeginFrame(TimeSpan.Zero, 20), Is.True);

		// Half a second passes in one frame. Catching up would draw on each of the next several frames.
		var afterStall = TimeSpan.FromSeconds(0.5);
		Assert.That(limiter.TryBeginFrame(afterStall, 20), Is.True);
		Assert.That(limiter.TryBeginFrame(afterStall + DisplayInterval, 20), Is.False);
		Assert.That(limiter.TryBeginFrame(afterStall + interval, 20), Is.True);
	}

	[Test]
	public void ResetMakesTheNextFrameDue()
	{
		var limiter = new ViewportFrameLimiter();
		Assert.That(limiter.TryBeginFrame(TimeSpan.Zero, 20), Is.True);
		Assert.That(limiter.TryBeginFrame(DisplayInterval, 20), Is.False);

		limiter.Reset();

		Assert.That(limiter.TryBeginFrame(DisplayInterval * 2, 20), Is.True);
	}
}
