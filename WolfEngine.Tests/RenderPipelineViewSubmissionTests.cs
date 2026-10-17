using System.Numerics;
using System.Reflection;
using WolfEngine;
using WolfEngine.ECS;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Tests;

public sealed class RenderPipelineViewSubmissionTests
{
	[Test]
	public void PublishSnapshot_GathersTwoBoundWorldsIntoIsolatedViewEntries()
	{
		var (graph, _) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(new RenderGraphResourceRegistry());
		var firstWorld = CreateWorldWithLight(1.0f);
		var secondWorld = CreateWorldWithLight(2.0f);
		var first = graph.CreateView(new RenderViewDescriptor(firstWorld, "first", RenderViewOutput.Texture));
		var second = graph.CreateView(new RenderViewDescriptor(secondWorld, "second", RenderViewOutput.Texture));
		var pipeline = new RenderPipeline(graph);
		var camera = new Camera();
		var transform = new WorldTransform { LocalToWorld = Matrix4x4.Identity };

		pipeline.PublishSnapshot([
			new RenderViewSubmission(second, camera, transform, new RenderConfig()),
			new RenderViewSubmission(first, camera, transform, new RenderConfig())
		]);
		var snapshot = ConsumeLatest(graph);

		Assert.Multiple(() =>
		{
			Assert.That(snapshot.Views.Select(view => view.View), Is.EqualTo(new[] { first, second }));
			Assert.That(snapshot.Views[0].BoundWorld, Is.SameAs(firstWorld));
			Assert.That(snapshot.Views[1].BoundWorld, Is.SameAs(secondWorld));
			Assert.That(snapshot.Views[0].LightPackets, Has.Count.EqualTo(1));
			Assert.That(snapshot.Views[1].LightPackets, Has.Count.EqualTo(1));
			Assert.That(snapshot.Views[0].LightPackets[0].Transform.Translation.X,
				Is.EqualTo(1.0f).Within(0.0001f));
			Assert.That(snapshot.Views[1].LightPackets[0].Transform.Translation.X,
				Is.EqualTo(2.0f).Within(0.0001f));
			Assert.That(snapshot.Views[0].GpuDrawDatabase,
				Is.Not.SameAs(snapshot.Views[1].GpuDrawDatabase));
		});
	}

	[Test]
	public void PublishSnapshot_RejectsDuplicateViewSubmissions()
	{
		var (graph, _) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(new RenderGraphResourceRegistry());
		var world = CreateWorldWithLight(1.0f);
		var view = graph.CreateView(new RenderViewDescriptor(world, "scene", RenderViewOutput.Texture));
		var submission = new RenderViewSubmission(
			view,
			new Camera(),
			new WorldTransform { LocalToWorld = Matrix4x4.Identity },
			new RenderConfig());

		var error = Assert.Throws<ArgumentException>(
			() => new RenderPipeline(graph).PublishSnapshot([submission, submission]));

		Assert.That(error!.Message, Does.Contain("more than once"));
	}

	private static World CreateWorldWithLight(float x)
	{
		var world = new World(WorldTag.All);
		var entity = world.CreateEntity();
		world.AddComponent(entity, new WorldTransform { LocalToWorld = Matrix4x4.CreateTranslation(x, 0.0f, 0.0f) });
		world.AddComponent(entity, new Light { Type = LightType.Point });
		return world;
	}

	private static FrameSnapshot ConsumeLatest(RenderGraph graph)
	{
		var field = typeof(RenderGraph).GetField("_snapshotBuffer", BindingFlags.Instance | BindingFlags.NonPublic)
		            ?? throw new AssertionException("RenderGraph snapshot buffer was not found.");
		var buffer = (FrameSnapshotBuffer)field.GetValue(graph)!;
		Assert.That(buffer.TryConsumeLatest(out var snapshot), Is.True);
		return snapshot;
	}
}
