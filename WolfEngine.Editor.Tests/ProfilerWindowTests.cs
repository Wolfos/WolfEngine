using WolfEngine.Editor.UI;
using WolfEngine.Profiling;

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
