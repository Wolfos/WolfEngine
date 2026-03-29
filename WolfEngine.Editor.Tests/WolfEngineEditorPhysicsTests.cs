using WolfEngine.ECS;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class WolfEngineEditorPhysicsTests
{
	[Test]
	public void CanAdvancePhysics_OnlyAllowsPlayingStateWithMatchingRuntimeWorld()
	{
		var runtimeWorld = new World(WorldTag.Game);

		Assert.That(WolfEngineEditor.CanAdvancePhysics(EditorPlayState.Edit, runtimeWorld, runtimeWorld), Is.False);
		Assert.That(WolfEngineEditor.CanAdvancePhysics(EditorPlayState.Paused, runtimeWorld, runtimeWorld), Is.False);
		Assert.That(WolfEngineEditor.CanAdvancePhysics(EditorPlayState.Playing, null, runtimeWorld), Is.False);
		Assert.That(WolfEngineEditor.CanAdvancePhysics(EditorPlayState.Playing, runtimeWorld, new World(WorldTag.Game)), Is.False);
		Assert.That(WolfEngineEditor.CanAdvancePhysics(EditorPlayState.Playing, runtimeWorld, runtimeWorld), Is.True);
	}
}
