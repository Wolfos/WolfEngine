using WolfEngine.Editor.UI;
using WolfEngine.Profiling;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class ProfilerWindowTests
{
	[Test]
	public void FrameProfiler_RecordsNonNegativeAllocationDeltas()
	{
		var profiler = new FrameProfiler();

		profiler.BeginFrame("Test Frame");
		using (profiler.Measure("Allocate"))
		{
			var payload = new byte[32 * 1024];
			GC.KeepAlive(payload);
		}
		profiler.EndFrame();

		var frame = profiler.GetLastFrames().Single();
		var sample = frame.Root.Children.Single();

		Assert.That(sample.AllocatedBytes, Is.GreaterThan(0));
		Assert.That(frame.Root.AllocatedBytes, Is.GreaterThanOrEqualTo(sample.AllocatedBytes));
	}

	[Test]
	public void FrameProfiler_TracksNestedScopesIndependently()
	{
		var profiler = new FrameProfiler();

		profiler.BeginFrame("Nested Frame");
		using (profiler.Measure("Outer"))
		{
			var outerPayload = new byte[8 * 1024];
			GC.KeepAlive(outerPayload);

			using (profiler.Measure("Inner"))
			{
				var innerPayload = new byte[16 * 1024];
				GC.KeepAlive(innerPayload);
			}

			var tailPayload = new byte[4 * 1024];
			GC.KeepAlive(tailPayload);
		}
		profiler.EndFrame();

		var frame = profiler.GetLastFrames().Single();
		var outer = frame.Root.Children.Single();
		var inner = outer.Children.Single();

		Assert.That(inner.AllocatedBytes, Is.GreaterThan(0));
		Assert.That(outer.AllocatedBytes, Is.GreaterThan(inner.AllocatedBytes));
		Assert.That(frame.Root.AllocatedBytes, Is.GreaterThanOrEqualTo(outer.AllocatedBytes));
	}

    [Test]
    public void FrameProfiler_BatchedTimingStaysInsideTheMeasuredParent()
    {
        var profiler = new FrameProfiler();
        profiler.RecordElapsed("Outside frame", 10);
        profiler.BeginFrame();
        using (profiler.Measure("Batch"))
        {
            profiler.RecordElapsed("Sampling", System.Diagnostics.Stopwatch.Frequency / 1000);
        }
        profiler.EndFrame();
        var batch = profiler.GetLastFrames().Single().Root.Children.Single();
        Assert.That(batch.Name, Is.EqualTo("Batch"));
        Assert.That(batch.Children.Single().DurationMs, Is.EqualTo(1).Within(.0001));
    }

	[Test]
	public void AggregateChildren_SumsAllocationBytesForRepeatedSiblingNames()
	{
		var root = new FrameProfiler.ProfileNode("Frame");
		root.Children.Add(CreateNode("Update", 1_000, 256, CreateNode("Physics", 250, 64)));
		root.Children.Add(CreateNode("Update", 2_000, 768, CreateNode("Physics", 500, 128)));
		root.Children.Add(CreateNode("Render", 3_000, 1_024));

		var aggregated = ProfilerWindowModelBuilder.AggregateChildren(root);

		Assert.That(aggregated, Has.Count.EqualTo(2));

		var update = aggregated.Single(node => node.Name == "Update");
		Assert.That(update.AllocatedBytes, Is.EqualTo(1_024));
		Assert.That(update.Children, Has.Count.EqualTo(1));
		Assert.That(update.Children[0].Name, Is.EqualTo("Physics"));
		Assert.That(update.Children[0].AllocatedBytes, Is.EqualTo(192));

		var render = aggregated.Single(node => node.Name == "Render");
		Assert.That(render.AllocatedBytes, Is.EqualTo(1_024));
	}

	[Test]
	public void FormatAllocatedBytes_UsesReadableUnits()
	{
		Assert.That(ProfilerWindowModelBuilder.FormatAllocatedBytes(0), Is.EqualTo("0 B"));
		Assert.That(ProfilerWindowModelBuilder.FormatAllocatedBytes(1536), Is.EqualTo("1.50 KB"));
		Assert.That(ProfilerWindowModelBuilder.FormatAllocatedBytes(2 * 1024 * 1024), Is.EqualTo("2.00 MB"));
	}

	[Test]
	public void GpuProfiler_DefaultsToDisabled()
	{
		var profiler = new GpuProfiler();
		profiler.SetBackendAvailability(true, null);

		Assert.That(profiler.Enabled, Is.False);
		Assert.That(profiler.BeginFrame(1), Is.Null);
	}

	[Test]
	public void GpuProfiler_PublishesCompleteFrameInSubmissionOrder()
	{
		var profiler = new GpuProfiler { Enabled = true };
		profiler.SetBackendAvailability(true, null);
		var frame = profiler.BeginFrame(42)!;
		var first = frame.AddPass("First");
		var second = frame.AddPass("Second");
		frame.Seal();

		second.Complete(new[] { new GpuProfileScope("CSSecond", 2.0) });
		Assert.That(profiler.LatestFrame, Is.Null);

		first.Complete(new[]
		{
			new GpuProfileScope("VS + PS", 1.0),
			new GpuProfileScope("Variant", 0.5)
		});

		var result = profiler.LatestFrame;
		Assert.That(result, Is.Not.Null);
		Assert.That(result!.FrameIndex, Is.EqualTo(42));
		Assert.That(result.DurationMs, Is.EqualTo(3.5));
		Assert.That(result.Passes.Select(pass => pass.Name), Is.EqualTo(new[] { "First", "Second" }));
		Assert.That(result.Passes[0].DurationMs, Is.EqualTo(1.5));
	}

	[Test]
	public void GpuProfiler_DisablingPreservesLatestFrame()
	{
		var profiler = new GpuProfiler { Enabled = true };
		profiler.SetBackendAvailability(true, null);
		var frame = profiler.BeginFrame(7)!;
		frame.AddPass("Pass").Complete(new[] { new GpuProfileScope("Shader", 1.25) });
		frame.Seal();
		var captured = profiler.LatestFrame;

		profiler.Enabled = false;

		Assert.That(profiler.LatestFrame, Is.SameAs(captured));
		Assert.That(profiler.BeginFrame(8), Is.Null);
	}

	[Test]
	public void GpuProfiler_LateOlderFrameDoesNotReplaceNewerFrame()
	{
		var profiler = new GpuProfiler { Enabled = true };
		profiler.SetBackendAvailability(true, null);
		var older = profiler.BeginFrame(10)!;
		var olderPass = older.AddPass("Older");
		older.Seal();
		var newer = profiler.BeginFrame(11)!;
		newer.AddPass("Newer").Complete(new[] { new GpuProfileScope("Shader", 1.0) });
		newer.Seal();

		olderPass.Complete(new[] { new GpuProfileScope("Shader", 2.0) });

		Assert.That(profiler.LatestFrame!.FrameIndex, Is.EqualTo(11));
	}

	[Test]
	public void GpuProfiler_UnsupportedBackendDisablesCapture()
	{
		var profiler = new GpuProfiler { Enabled = true };

		profiler.SetBackendAvailability(false, "Unsupported");

		Assert.That(profiler.Enabled, Is.False);
		Assert.That(profiler.UnsupportedReason, Is.EqualTo("Unsupported"));
		Assert.That(profiler.BeginFrame(1), Is.Null);
	}

	[Test]
	public void GpuProfileNames_UseVariantThenStageEntries()
	{
		var variant = new PipelineKey(
			PassKind.Compute, null, null, "ProfilerWindowTestCS", default, default, default,
			shaderVariant: "clustered_lighting.compute.slang");
		var compute = new PipelineKey(PassKind.Compute, null, null, "CSCull", default, default, default);
		var graphics = new PipelineKey(PassKind.Graphics, "VSMain", "PSMain", null, default, default, default);

		Assert.That(GpuProfileNames.FromPipeline(variant), Is.EqualTo("clustered_lighting.compute.slang"));
		Assert.That(GpuProfileNames.FromPipeline(compute), Is.EqualTo("CSCull"));
		Assert.That(GpuProfileNames.FromPipeline(graphics), Is.EqualTo("VSMain + PSMain"));
	}

	[Test]
	public void FormatGpuTime_UsesFramePercentage()
	{
		Assert.That(ProfilerWindowModelBuilder.FormatGpuTime(2.5, 10.0), Is.EqualTo("2.50 ms (25.0%)"));
	}

	[Test]
	public void SpikeWindow_ExpiresMaximumAndFallsBackToNextCandidate()
	{
		var clock = new ProfilerTestClock();
		var window = new SpikeFrameWindow<string>(clock);
		window.Add("spike", 40, clock.GetTimestamp());
		clock.Advance(1);
		window.Add("next", 20, clock.GetTimestamp());
		clock.Advance(1);
		window.Add("current", 5, clock.GetTimestamp());
		Assert.That(window.TryGet(clock.GetTimestamp(), out var frame), Is.True);
		Assert.That(frame, Is.EqualTo("spike"));
		clock.Advance(3);
		Assert.That(window.TryGet(clock.GetTimestamp(), out frame), Is.True);
		Assert.That(frame, Is.EqualTo("next"));
		clock.Advance(2);
		Assert.That(window.TryGet(clock.GetTimestamp(), out _), Is.False);
	}

	[Test]
	public void FrameProfiler_PausePreservesTreesAndDiscardsInFlightFrame()
	{
		var clock = new ProfilerTestClock();
		var profiler = new FrameProfiler(clock);
		Assert.That(profiler.Enabled, Is.True);
		profiler.BeginFrame();
		using (profiler.Measure("Sporadic work")) { }
		profiler.EndFrame();
		var captured = profiler.GetLastFrames().Single().Root;
		profiler.BeginFrame("In flight");
		profiler.Enabled = false;
		using (profiler.Measure("Disabled")) { }
		profiler.EndFrame();
		clock.Advance(10);
		profiler.BeginFrame("Disabled");
		profiler.RecordElapsed("Disabled", 100);
		profiler.EndFrame();
		Assert.That(profiler.GetLastFrames().Single().Root, Is.SameAs(captured));
		Assert.That(profiler.GetSpikeFrames().Single().Root, Is.SameAs(captured));
		Assert.That(captured.Children.Single().Name, Is.EqualTo("Sporadic work"));
		profiler.Enabled = true;
		Assert.That(profiler.GetSpikeFrames(), Is.Empty);
		profiler.BeginFrame("Resumed");
		profiler.EndFrame();
		Assert.That(profiler.GetSpikeFrames().Single().Root.Name, Is.EqualTo("Resumed"));
		clock.Advance(5);
		Assert.That(profiler.GetSpikeFrames(), Is.Empty);
	}

	[Test]
	public void FrameProfiler_PauseAndResumeDuringFrameDiscardsPartialCapture()
	{
		var profiler = new FrameProfiler();
		profiler.BeginFrame();
		profiler.EndFrame();
		var captured = profiler.GetLastFrames().Single().Root;
		profiler.BeginFrame();
		profiler.Enabled = false;
		profiler.Enabled = true;
		using (profiler.Measure("Partial")) { }
		profiler.EndFrame();
		Assert.That(profiler.GetLastFrames().Single().Root, Is.SameAs(captured));
	}

	[Test]
	public void GpuProfiler_SpikeUsesFiveSecondsAndPauseRejectsLateResults()
	{
		var clock = new ProfilerTestClock();
		var profiler = new GpuProfiler(clock) { Enabled = true };
		CompleteGpuFrame(profiler, 1, 40);
		clock.Advance(1);
		CompleteGpuFrame(profiler, 2, 10);
		Assert.That(profiler.LatestFrame!.FrameIndex, Is.EqualTo(2));
		Assert.That(profiler.SpikeFrame!.FrameIndex, Is.EqualTo(1));
		clock.Advance(4);
		Assert.That(profiler.SpikeFrame!.FrameIndex, Is.EqualTo(2));
		var pending = profiler.BeginFrame(3)!;
		var pass = pending.AddPass("Pending");
		pending.Seal();
		profiler.RecordingEnabled = false;
		clock.Advance(10);
		pass.Complete(new[] { new GpuProfileScope("Late", 100) });
		Assert.That(profiler.BeginFrame(4), Is.Null);
		Assert.That(profiler.LatestFrame!.FrameIndex, Is.EqualTo(2));
		Assert.That(profiler.SpikeFrame!.FrameIndex, Is.EqualTo(2));
		profiler.RecordingEnabled = true;
		Assert.That(profiler.SpikeFrame, Is.Null);
		CompleteGpuFrame(profiler, 5, 5);
		Assert.That(profiler.SpikeFrame!.FrameIndex, Is.EqualTo(5));
		clock.Advance(5);
		Assert.That(profiler.SpikeFrame, Is.Null);
	}

	private static void CompleteGpuFrame(GpuProfiler profiler, ulong index, double duration)
	{
		var capture = profiler.BeginFrame(index)!;
		capture.AddPass("Pass").Complete(new[] { new GpuProfileScope("Shader", duration) });
		capture.Seal();
	}

	private sealed class ProfilerTestClock : TimeProvider
	{
		private long _timestamp;
		public override long TimestampFrequency => 1000;
		public override long GetTimestamp() => _timestamp;
		public void Advance(int seconds) => _timestamp += seconds * TimestampFrequency;
	}

	private static FrameProfiler.ProfileNode CreateNode(string name, long durationTicks, long allocatedBytes, params FrameProfiler.ProfileNode[] children)
	{
		var node = new FrameProfiler.ProfileNode(name)
		{
			StartTicks = 100,
			EndTicks = 100 + durationTicks,
			StartAllocatedBytes = 1_000,
			EndAllocatedBytes = 1_000 + allocatedBytes
		};

		node.Children.AddRange(children);
		return node;
	}
}
