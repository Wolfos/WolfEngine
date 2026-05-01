namespace WolfEngine.Editor.UI;

internal enum SceneShortcutCommand
{
	None,
	SelectTranslate,
	SelectRotate,
	SelectScale,
	SelectRaiseLower,
	SelectFlatten,
	SelectSmooth,
	SelectBrush,
	SelectEyedropper,
	SelectPen
}

internal readonly record struct SceneShortcutSnapshot(
	bool ShortcutTargetActive,
	bool PrimaryModifierDown,
	bool TranslatePressed,
	bool RotatePressed,
	bool ScalePressed,
	bool Tool1Pressed,
	bool Tool2Pressed,
	bool Tool3Pressed,
	bool Tool4Pressed,
	bool Tool5Pressed,
	bool Tool6Pressed,
	bool IsTextInputActive,
	SceneToolMode CurrentMode);

internal static class SceneShortcutResolver
{
	public static SceneShortcutCommand Resolve(SceneShortcutSnapshot snapshot)
	{
		if (snapshot.ShortcutTargetActive == false ||
		    snapshot.IsTextInputActive ||
		    snapshot.PrimaryModifierDown)
		{
			return SceneShortcutCommand.None;
		}

		if (snapshot.TranslatePressed)
		{
			return SceneShortcutCommand.SelectTranslate;
		}

		if (snapshot.RotatePressed)
		{
			return SceneShortcutCommand.SelectRotate;
		}

		if (snapshot.ScalePressed)
		{
			return SceneShortcutCommand.SelectScale;
		}

		if (snapshot.CurrentMode != SceneToolMode.Terrain)
		{
			return SceneShortcutCommand.None;
		}

		if (snapshot.Tool1Pressed)
		{
			return SceneShortcutCommand.SelectRaiseLower;
		}

		if (snapshot.Tool2Pressed)
		{
			return SceneShortcutCommand.SelectFlatten;
		}

		if (snapshot.Tool3Pressed)
		{
			return SceneShortcutCommand.SelectSmooth;
		}

		if (snapshot.Tool4Pressed)
		{
			return SceneShortcutCommand.SelectBrush;
		}

		if (snapshot.Tool5Pressed)
		{
			return SceneShortcutCommand.SelectEyedropper;
		}

		if (snapshot.Tool6Pressed)
		{
			return SceneShortcutCommand.SelectPen;
		}

		return SceneShortcutCommand.None;
	}
}
