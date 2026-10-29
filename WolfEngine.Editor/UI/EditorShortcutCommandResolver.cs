namespace WolfEngine.Editor.UI;

internal enum EditorShortcutCommand
{
	None,
	Undo,
	Redo,
	NewScene,
	SaveScene,
	RefreshAssetDatabase,
	DuplicateFocusedSelection,
	ClearEntitySelection,
	DeleteFocusedSelection
}

internal readonly record struct EditorShortcutSnapshot(
	bool PrimaryModifierDown,
	bool ShiftDown,
	bool UndoPressed,
	bool RedoPressed,
	bool NewPressed,
	bool SavePressed,
	bool RefreshPressed,
	bool DuplicatePressed,
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
			if (snapshot.UndoPressed)
			{
				return snapshot.IsMacOS && snapshot.ShiftDown
					? EditorShortcutCommand.Redo
					: EditorShortcutCommand.Undo;
			}

			if (snapshot.IsMacOS == false && snapshot.RedoPressed)
			{
				return EditorShortcutCommand.Redo;
			}

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

			if (snapshot.DuplicatePressed &&
			    (focusedWindow == EditorFocusedWindow.Entities || focusedWindow == EditorFocusedWindow.Document))
			{
				return EditorShortcutCommand.DuplicateFocusedSelection;
			}
		}

		if (snapshot.ShiftDown && snapshot.DuplicatePressed && focusedWindow != EditorFocusedWindow.Document)
		{
			return EditorShortcutCommand.ClearEntitySelection;
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
			EditorFocusedWindow.Document => EditorShortcutCommand.DeleteFocusedSelection,
			_ => EditorShortcutCommand.None
		};
	}
}
