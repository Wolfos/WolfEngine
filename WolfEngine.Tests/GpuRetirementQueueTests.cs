using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Tests;

public sealed class GpuRetirementQueueTests
{
	[Test]
	public void Retirement_ReleasesOnlyAfterSealedSubmissionCompletes()
	{
		var queue = new GpuRetirementQueue();
		var released = false;
		queue.Retire(() => released = true, "texture");

		var batch = queue.PrepareSubmission(GpuSubmissionKind.PrimaryFrame);
		queue.SealSubmission(batch, 7);
		queue.ReleaseCompleted(6);

		Assert.Multiple(() =>
		{
			Assert.That(released, Is.False);
			Assert.That(queue.Stats.UnsealedCount, Is.Zero);
			Assert.That(queue.Stats.PendingCount, Is.EqualTo(1));
		});

		queue.ReleaseCompleted(7);

		Assert.Multiple(() =>
		{
			Assert.That(released, Is.True);
			Assert.That(queue.Stats.PendingCount, Is.Zero);
			Assert.That(queue.Stats.ReleasedCount, Is.EqualTo(1));
		});
	}

	[Test]
	public void RetirementQueuedDuringSubmission_WaitsForFollowingSubmission()
	{
		var queue = new GpuRetirementQueue();
		var firstReleased = false;
		var secondReleased = false;
		queue.Retire(() => firstReleased = true, "first");

		var firstBatch = queue.PrepareSubmission(GpuSubmissionKind.PrimaryFrame);
		queue.Retire(() => secondReleased = true, "second");
		queue.SealSubmission(firstBatch, 3);
		queue.ReleaseCompleted(3);

		Assert.Multiple(() =>
		{
			Assert.That(firstReleased, Is.True);
			Assert.That(secondReleased, Is.False);
			Assert.That(queue.Stats.UnsealedCount, Is.EqualTo(1));
		});

		var secondBatch = queue.PrepareSubmission(GpuSubmissionKind.PrimaryFrame);
		queue.SealSubmission(secondBatch, 4);
		queue.ReleaseCompleted(4);

		Assert.That(secondReleased, Is.True);
	}

	[Test]
	public void AuxiliarySubmission_DoesNotSealPrimaryFrameRetirements()
	{
		var queue = new GpuRetirementQueue();
		var released = false;
		queue.Retire(() => released = true, "frame resource");

		var auxiliaryBatch = queue.PrepareSubmission(GpuSubmissionKind.Auxiliary);
		queue.SealSubmission(auxiliaryBatch, 3);
		queue.ReleaseCompleted(3);

		Assert.Multiple(() =>
		{
			Assert.That(released, Is.False);
			Assert.That(queue.Stats.UnsealedCount, Is.EqualTo(1));
			Assert.That(queue.Stats.PendingCount, Is.Zero);
		});

		var frameBatch = queue.PrepareSubmission(GpuSubmissionKind.PrimaryFrame);
		queue.SealSubmission(frameBatch, 4);
		queue.ReleaseCompleted(4);

		Assert.That(released, Is.True);
	}

	[Test]
	public void AlreadySubmittedResource_CanRetireAgainstItsIssuedSubmission()
	{
		var queue = new GpuRetirementQueue();
		var released = false;

		queue.RetireAfterSubmission(() => released = true, "previous frame", 12);
		queue.ReleaseCompleted(11);
		Assert.That(released, Is.False);

		queue.ReleaseCompleted(12);
		Assert.That(released, Is.True);
	}

	[Test]
	public void FailedSubmission_RestoresPreparedRetirements()
	{
		var queue = new GpuRetirementQueue();
		var releaseOrder = new List<int>();
		queue.Retire(() => releaseOrder.Add(1), "first");

		var failedBatch = queue.PrepareSubmission(GpuSubmissionKind.PrimaryFrame);
		queue.Retire(() => releaseOrder.Add(2), "second");
		queue.CancelSubmission(failedBatch);

		var successfulBatch = queue.PrepareSubmission(GpuSubmissionKind.PrimaryFrame);
		queue.SealSubmission(successfulBatch, 9);
		queue.ReleaseCompleted(9);

		Assert.That(releaseOrder, Is.EqualTo(new[] { 1, 2 }));
	}

	[Test]
	public void WaitForIdle_ReleasesPendingAndUnsealedRetirements()
	{
		var queue = new GpuRetirementQueue();
		var releaseCount = 0;
		queue.Retire(() => releaseCount++, "pending");
		var batch = queue.PrepareSubmission(GpuSubmissionKind.PrimaryFrame);
		queue.SealSubmission(batch, 2);
		queue.Retire(() => releaseCount++, "unsealed");

		queue.ReleaseAllAfterIdle();

		Assert.Multiple(() =>
		{
			Assert.That(releaseCount, Is.EqualTo(2));
			Assert.That(queue.Stats.UnsealedCount, Is.Zero);
			Assert.That(queue.Stats.PendingCount, Is.Zero);
			Assert.That(queue.Stats.ReleasedCount, Is.EqualTo(2));
		});
	}

	[Test]
	public void ReleaseFailure_DoesNotPreventLaterRetirements()
	{
		var queue = new GpuRetirementQueue();
		var laterReleased = false;
		queue.Retire(() => throw new InvalidOperationException("expected"), "failing");
		queue.Retire(() => laterReleased = true, "later");
		var batch = queue.PrepareSubmission(GpuSubmissionKind.PrimaryFrame);
		queue.SealSubmission(batch, 1);

		Assert.Throws<AggregateException>(() => queue.ReleaseCompleted(1));
		Assert.Multiple(() =>
		{
			Assert.That(laterReleased, Is.True);
			Assert.That(queue.Stats.PendingCount, Is.Zero);
			Assert.That(queue.Stats.ReleasedCount, Is.EqualTo(2));
		});
	}
}
