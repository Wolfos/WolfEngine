namespace WolfEngine.Editor.UI;

internal enum EditorShortcutCommand
{
	None,
	NewScene,
	SaveScene,
	RefreshAssetDatabase,
	DeleteFocusedSelection
}

internal readonly record struct EditorShortcutSnapshot(
	bool PrimaryModifierDown,
	bool NewPressed,
	bool SavePressed,
	bool RefreshPressed,
	bool DeletePressed,
	bool BackspacePressed,
	bool IsTextInputActive,
	bool IsMacOS);

internal static class EditorShortcutCommandResolver
{
	public static EditorShortcutCommand Resolve(EditorShortcutSnapshot snapshot, EditorFocusedWindow focusedWindow)
	{
		if (snapshot.IsTextInputActive)
		{
			return EditorShortcutCommand.None;
		}

		if (snapshot.PrimaryModifierDown)
		{
			if (snapshot.NewPressed)
			{
				return EditorShortcutCommand.NewScene;
			}

			if (snapshot.SavePressed)
			{
				return EditorShortcutCommand.SaveScene;
			}

			if (snapshot.RefreshPressed)
			{
				return EditorShortcutCommand.RefreshAssetDatabase;
			}
		}

		var deletePressed = snapshot.DeletePressed || (snapshot.IsMacOS && snapshot.PrimaryModifierDown && snapshot.BackspacePressed);
		if (deletePressed == false)
		{
			return EditorShortcutCommand.None;
		}

		return focusedWindow switch
		{
			EditorFocusedWindow.Entities => EditorShortcutCommand.DeleteFocusedSelection,
			EditorFocusedWindow.Assets => EditorShortcutCommand.DeleteFocusedSelection,
			_ => EditorShortcutCommand.None
		};
	}
}
