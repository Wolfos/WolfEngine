using WolfEngine.Editor.UI;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class SceneShortcutResolverTests
{
	[Test]
	public void Resolve_MapsTransformShortcuts_WhenSceneShortcutsAreAllowed()
	{
		Assert.That(
			SceneShortcutResolver.Resolve(new SceneShortcutSnapshot(
				true, false,
				true, false, false,
				false,
				false, false, false, false, false, false,
				false,
				SceneToolMode.Transform)),
			Is.EqualTo(SceneShortcutCommand.SelectTranslate));

		Assert.That(
			SceneShortcutResolver.Resolve(new SceneShortcutSnapshot(
				true, false,
				false, true, false,
				false,
				false, false, false, false, false, false,
				false,
				SceneToolMode.Transform)),
			Is.EqualTo(SceneShortcutCommand.SelectRotate));

		Assert.That(
			SceneShortcutResolver.Resolve(new SceneShortcutSnapshot(
				true, false,
				false, false, true,
				false,
				false, false, false, false, false, false,
				false,
				SceneToolMode.Transform)),
			Is.EqualTo(SceneShortcutCommand.SelectScale));

		Assert.That(
			SceneShortcutResolver.Resolve(new SceneShortcutSnapshot(
				true, false,
				false, false, false,
				true,
				false, false, false, false, false, false,
				false,
				SceneToolMode.Transform)),
			Is.EqualTo(SceneShortcutCommand.SelectTerrainMode));
	}

	[Test]
	public void Resolve_MapsTerrainToolShortcuts_InTerrainMode()
	{
		Assert.That(
			SceneShortcutResolver.Resolve(CreateTerrainSnapshot(tool1Pressed: true)),
			Is.EqualTo(SceneShortcutCommand.SelectRaiseLower));
		Assert.That(
			SceneShortcutResolver.Resolve(CreateTerrainSnapshot(tool2Pressed: true)),
			Is.EqualTo(SceneShortcutCommand.SelectFlatten));
		Assert.That(
			SceneShortcutResolver.Resolve(CreateTerrainSnapshot(tool3Pressed: true)),
			Is.EqualTo(SceneShortcutCommand.SelectSmooth));
		Assert.That(
			SceneShortcutResolver.Resolve(CreateTerrainSnapshot(tool4Pressed: true)),
			Is.EqualTo(SceneShortcutCommand.SelectBrush));
		Assert.That(
			SceneShortcutResolver.Resolve(CreateTerrainSnapshot(tool5Pressed: true)),
			Is.EqualTo(SceneShortcutCommand.SelectEyedropper));
		Assert.That(
			SceneShortcutResolver.Resolve(CreateTerrainSnapshot(tool6Pressed: true)),
			Is.EqualTo(SceneShortcutCommand.SelectPen));
	}

	[Test]
	public void Resolve_IgnoresTerrainToolShortcuts_OutsideTerrainMode()
	{
		var command = SceneShortcutResolver.Resolve(new SceneShortcutSnapshot(
			true, false,
			false, false, false,
			false,
			true, false, false, false, false, false,
			false,
			SceneToolMode.Transform));

		Assert.That(command, Is.EqualTo(SceneShortcutCommand.None));
	}

	[Test]
	public void Resolve_IgnoresShortcuts_WhenTextInputIsActive()
	{
		var command = SceneShortcutResolver.Resolve(new SceneShortcutSnapshot(
			true, false,
			true, false, false,
			false,
			true, true, true, true, true, true,
			true,
			SceneToolMode.Terrain));

		Assert.That(command, Is.EqualTo(SceneShortcutCommand.None));
	}

	[Test]
	public void Resolve_IgnoresShortcuts_WhenSceneWindowIsInactive()
	{
		var command = SceneShortcutResolver.Resolve(new SceneShortcutSnapshot(
			false, false,
			true, true, true,
			true,
			true, true, true, true, true, true,
			false,
			SceneToolMode.Terrain));

		Assert.That(command, Is.EqualTo(SceneShortcutCommand.None));
	}

	[Test]
	public void Resolve_IgnoresShortcuts_WhenPrimaryModifierIsHeld()
	{
		var command = SceneShortcutResolver.Resolve(new SceneShortcutSnapshot(
			true, true,
			true, true, true,
			true,
			true, true, true, true, true, true,
			false,
			SceneToolMode.Terrain));

		Assert.That(command, Is.EqualTo(SceneShortcutCommand.None));
	}

	private static SceneShortcutSnapshot CreateTerrainSnapshot(
		bool tool1Pressed = false,
		bool tool2Pressed = false,
		bool tool3Pressed = false,
		bool tool4Pressed = false,
		bool tool5Pressed = false,
		bool tool6Pressed = false)
	{
		return new SceneShortcutSnapshot(
			true, false,
			false, false, false,
			false,
			tool1Pressed, tool2Pressed, tool3Pressed, tool4Pressed, tool5Pressed, tool6Pressed,
			false,
			SceneToolMode.Terrain);
	}
}
