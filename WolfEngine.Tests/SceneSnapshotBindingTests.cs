using System.Numerics;
using System.Reflection;
using Moq;
using WolfEngine.ECS;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.UI;
using WolfEngine.Rendering;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class SceneSnapshotBindingTests
{
	[Test]
	public void PlayExitAfterSnapshotPublicationKeepsPresentedViewportUntilNewSnapshot()
	{
		var (graph, _) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(new RenderGraphResourceRegistry());
		var runtime = new World(WorldTag.Game);
		var authoring = new World(WorldTag.Authoring);
		var view = graph.CreateView(new RenderViewDescriptor(runtime, "scene", RenderViewOutput.Texture));
		Assert.That(graph.ViewRegistry.TryGet(view, out var state), Is.True);
		var presented = new SceneViewportRenderState(42, new Int2(640, 360), Matrix4x4.Identity, [], SceneDebugViewIds.FinalColor);
		state.ResolvedSceneViewportState = presented;
		var viewportBus = Get<EditorViewportStateBus>(graph, "_viewportStateBus");
		viewportBus.PublishRenderState(view, presented);
		Assert.That(graph.TryBeginSnapshotWrite(out var frame), Is.True);
		frame.BindView(view, runtime, state.BindingGeneration);
		Assert.That(graph.TryPublishSnapshot(), Is.True);
		// The Stop button runs during UI drawing, after the outgoing snapshot was published.
		graph.RebindView(view, authoring);
		var coordinator = Get<EditorFrameCoordinator>(graph, "_editorFrameCoordinator");
		for (var index = 0; index < 2; index++)
		{
			coordinator.PublishCompletedFrame();
			graph.OnRender(0);
			Assert.That(state.ResolvedSceneViewportState.TextureId, Is.EqualTo((nint)42));
			Assert.That(viewportBus.GetRenderState(view).TextureId, Is.EqualTo((nint)42));
		}
		var renderer = Mock.Get(Get<IRenderer>(graph, "_renderer"));
		renderer.Verify(value => value.BeginFrame(), Times.Never);
		Assert.That(Get<RenderFrameCoordinator>(graph, "_renderFrameCoordinator").CompletedSequence, Is.Zero);
		// Consuming the stale snapshot still frees the publication buffer for the authoring frame.
		Assert.That(graph.TryBeginSnapshotWrite(out var replacement), Is.True);
		var authoringSnapshot = replacement.BindView(view, authoring, state.BindingGeneration);
		Assert.That(RenderGraph.IsViewSnapshotCurrent(state, authoringSnapshot), Is.True);
		Assert.That(graph.TryPublishSnapshot(), Is.True);
	}

	private static T Get<T>(RenderGraph graph, string name) =>
		(T)typeof(RenderGraph).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(graph)!;

	[Test]
	public void ReboundViewRejectsOutgoingSnapshotUntilReplacementIsPublished()
	{
		var registry = new RenderViewRegistry();
		var first = new World(WorldTag.Game);
		var second = new World(WorldTag.Game);
		var state = registry.Create(first, "game", RenderViewOutput.Backbuffer);
		var frame = new FrameSnapshot();
		var outgoing = frame.BindView(state.View, first, state.BindingGeneration);
		Assert.That(RenderGraph.IsViewSnapshotCurrent(state, outgoing), Is.True);
		registry.Rebind(state.View, second);
		Assert.That(RenderGraph.IsViewSnapshotCurrent(state, outgoing), Is.False);
		var replacement = new FrameSnapshot().BindView(state.View, second, state.BindingGeneration);
		Assert.That(RenderGraph.IsViewSnapshotCurrent(state, replacement), Is.True);
		// Returning to the same world still needs a fresh snapshot, rather than accepting the original generation.
		registry.Rebind(state.View, first);
		Assert.That(RenderGraph.IsViewSnapshotCurrent(state, outgoing), Is.False);
	}
}
