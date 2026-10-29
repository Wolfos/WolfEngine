namespace WolfEngine.Editor.UI;

/// <summary>
/// An editor that owns its own document and undo history, such as the clip editor. While one has
/// shortcut focus, undo, redo and save act on its document instead of the scene, so a clip edit and a
/// scene edit never share an undo stack.
/// </summary>
public interface IEditorDocumentEditor
{
	/// <summary>True only while one of this editor's panels has keyboard focus.</summary>
	bool HasShortcutFocus { get; }

	bool Undo();
	bool Redo();
	bool Save();

	/// <summary>Deletes whatever is selected in the editor, such as keys.</summary>
	bool DeleteSelection();

	/// <summary>Duplicates whatever is selected in the editor.</summary>
	bool DuplicateSelection();

	/// <summary>Draws the editor's modal dialogs, such as an unsaved-changes prompt. Called once per frame.</summary>
	void DrawDialogs();
}
