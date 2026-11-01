namespace WolfEngine.Editor.UI;

/// <summary>
/// An editor with its own selection, such as the clip editor. While one has shortcut focus, delete and
/// duplicate act on its selection instead of the scene's. Save, undo and redo do not follow focus;
/// they act on the active workspace's owned <see cref="IEditorDocument"/>.
/// </summary>
public interface IEditorDocumentEditor
{
	/// <summary>True only while one of this editor's panels has keyboard focus.</summary>
	bool HasShortcutFocus { get; }

	/// <summary>Deletes whatever is selected in the editor, such as keys.</summary>
	bool DeleteSelection();

	/// <summary>Duplicates whatever is selected in the editor.</summary>
	bool DuplicateSelection();

	/// <summary>Draws the editor's modal dialogs, such as an unsaved-changes prompt. Called once per frame.</summary>
	void DrawDialogs();
}
