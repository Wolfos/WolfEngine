using WolfEngine.ECS;
using WolfEngine.Rendering;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class SceneSnapshotBindingTests
{
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
