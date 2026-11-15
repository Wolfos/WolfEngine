using WolfEngine.AssetPipeline;

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

/// <summary>
/// A document that edits one asset at a time and reopens it when the editor next opens the project. The
/// asset is remembered by GUID, so moving or renaming it while the editor is closed does not lose it.
/// </summary>
public interface IEditorReopenableDocument : IEditorDocument
{
	/// <summary>Raised with the asset now open, or <see cref="Guid.Empty"/> once the document closes.</summary>
	event Action<Guid>? OpenAssetChanged;

	/// <summary>
	/// Opens the asset this document had open in an earlier session, without changing workspace or focus.
	/// Returns false when the asset is not one this document edits or cannot be opened.
	/// </summary>
	bool Reopen(AssetDatabaseEntry asset);
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
