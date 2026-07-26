using System;
using System.Threading;
using System.Threading.Tasks;
using WolfEngine.Rendering;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class EditorFrameCoordinatorTests
{
	[Test]
	public void TryWaitForNextFrame_ReturnsPublishedFrame()
	{
		var coordinator = new EditorFrameCoordinator();
		coordinator.PublishCompletedFrame();

		var published = coordinator.TryWaitForNextFrame(0, static () => { }, out var sequence);

		Assert.That(published, Is.True);
		Assert.That(sequence, Is.EqualTo(1));
	}

	[Test]
	public void TryWaitForNextFrame_ReturnsFalseWhenShutdownIsRequestedFirst()
	{
		var coordinator = new EditorFrameCoordinator();
		coordinator.RequestShutdown();

		var published = coordinator.TryWaitForNextFrame(0, static () => { }, out _);

		Assert.That(published, Is.False);
		Assert.That(coordinator.IsShutdownRequested, Is.True);
	}

	[Test]
	public void TryWaitForNextFrame_StillReportsFramesPublishedBeforeShutdown()
	{
		var coordinator = new EditorFrameCoordinator();
		coordinator.PublishCompletedFrame();
		coordinator.RequestShutdown();

		var published = coordinator.TryWaitForNextFrame(0, static () => { }, out var sequence);

		Assert.That(published, Is.True, "A frame published before shutdown still has to be rendered.");
		Assert.That(sequence, Is.EqualTo(1));
		Assert.That(coordinator.TryWaitForNextFrame(sequence, static () => { }, out _), Is.False);
	}

	[Test]
	public void RequestShutdown_ReleasesAWaiterThatIsAlreadyBlocked()
	{
		// This is the frame-capture shutdown path: the producer stops between two published frames
		// while the render thread is already parked inside the wait.
		var coordinator = new EditorFrameCoordinator();
		var entered = new ManualResetEventSlim(false);
		var wait = Task.Run(() =>
		{
			return coordinator.TryWaitForNextFrame(0, entered.Set, out _);
		});

		Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True, "The waiter never started waiting.");
		Assert.That(wait.Wait(TimeSpan.FromMilliseconds(200)), Is.False, "The waiter returned without a frame.");

		coordinator.RequestShutdown();

		Assert.That(wait.Wait(TimeSpan.FromSeconds(10)), Is.True, "RequestShutdown did not release the waiter.");
		Assert.That(wait.Result, Is.False);
	}
}
