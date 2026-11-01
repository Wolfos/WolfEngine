namespace WolfEngine.Editor.UI;

/// <summary>
/// The kind of document a workspace owns. File > Save, Edit > Undo and Redo, their shortcuts and the
/// workspace tab's unsaved-changes dot all act on the active workspace's owned document.
/// </summary>
public enum EditorDocumentKind
{
	None,
	Scene,
	AnimationGraph,
	AnimationClip
}

/// <summary>
/// A document with its own save state and undo history. Each <see cref="EditorDocumentKind"/> has one,
/// which edits whatever file of that kind is open.
/// </summary>
public interface IEditorDocument
{
	EditorDocumentKind Kind { get; }

	/// <summary>The open file's name for menus, or null when nothing of this kind is open.</summary>
	string? DisplayName { get; }

	bool IsDirty { get; }
	bool CanSave { get; }
	bool CanUndo { get; }
	bool CanRedo { get; }

	bool Save();
	bool Undo();
	bool Redo();
}

public static class EditorDocumentKindNames
{
	public static string Get(EditorDocumentKind kind)
	{
		return kind switch
		{
			EditorDocumentKind.Scene => "Scene",
			EditorDocumentKind.AnimationGraph => "Animation Graph",
			EditorDocumentKind.AnimationClip => "Animation Clip",
			_ => "None"
		};
	}
}
