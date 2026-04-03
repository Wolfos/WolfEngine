using System;
using System.Collections.Generic;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

public interface IEditorUndoRedoService
{
	bool CanUndo { get; }
	bool CanRedo { get; }

	void BeginCapture(string description);
	bool CommitCapture(IEditorUndoRedoEntry entry);
	void CancelCapture();
	bool Undo();
	bool Redo();
	void Clear();
}

public interface IEditorUndoRedoEntry
{
	string Description { get; }
	void Undo(EditorUndoRedoContext context);
	void Redo(EditorUndoRedoContext context);
}

public sealed class EditorUndoRedoContext
{
	public required IEditorSceneWorkspace SceneWorkspace { get; init; }
	public required IEditorInteractionState InteractionState { get; init; }
	public required IEditorSceneSnapshotService SceneSnapshotService { get; init; }
	public required IEditorAssetSnapshotService AssetSnapshotService { get; init; }
}

public readonly record struct EditorAssetFileSnapshot(
	Guid AssetId,
	string RelativeAssetPath,
	string RelativeSourcePath,
	string Json);

public sealed class SceneComponentEditUndoRedoEntry : IEditorUndoRedoEntry
{
	private readonly IReadOnlyList<SceneComponentSnapshot> _before;
	private readonly IReadOnlyList<SceneComponentSnapshot> _after;

	public SceneComponentEditUndoRedoEntry(string description, IReadOnlyList<SceneComponentSnapshot> before, IReadOnlyList<SceneComponentSnapshot> after)
	{
		Description = string.IsNullOrWhiteSpace(description) ? "Edit Component" : description;
		_before = before ?? throw new ArgumentNullException(nameof(before));
		_after = after ?? throw new ArgumentNullException(nameof(after));
	}

	public string Description { get; }

	public void Undo(EditorUndoRedoContext context)
	{
		context.SceneSnapshotService.ApplyComponentSnapshots(context.SceneWorkspace.CurrentScene, _before);
		context.InteractionState.MarkSceneDirty();
	}

	public void Redo(EditorUndoRedoContext context)
	{
		context.SceneSnapshotService.ApplyComponentSnapshots(context.SceneWorkspace.CurrentScene, _after);
		context.InteractionState.MarkSceneDirty();
	}
}

public sealed class EntityDeletionUndoRedoEntry : IEditorUndoRedoEntry
{
	private readonly IReadOnlyList<DeletedEntitySnapshot> _deletedEntities;

	public EntityDeletionUndoRedoEntry(string description, IReadOnlyList<DeletedEntitySnapshot> deletedEntities)
	{
		Description = string.IsNullOrWhiteSpace(description) ? "Delete Entity" : description;
		_deletedEntities = deletedEntities ?? throw new ArgumentNullException(nameof(deletedEntities));
	}

	public string Description { get; }

	public void Undo(EditorUndoRedoContext context)
	{
		context.SceneSnapshotService.RestoreDeletedEntities(context.SceneWorkspace.CurrentScene, _deletedEntities);
		context.InteractionState.MarkSceneDirty();
	}

	public void Redo(EditorUndoRedoContext context)
	{
		context.SceneSnapshotService.DeleteEntitiesByPersistentIds(context.SceneWorkspace.CurrentScene, GetDeletedEntityIds());
		context.InteractionState.MarkSceneDirty();
	}

	private IReadOnlyList<Guid> GetDeletedEntityIds()
	{
		var ids = new List<Guid>(_deletedEntities.Count);
		for (var i = 0; i < _deletedEntities.Count; i++)
		{
			ids.Add(_deletedEntities[i].Entity.EntityId);
		}

		return ids;
	}
}

public sealed class MaterialAssetEditUndoRedoEntry : IEditorUndoRedoEntry
{
	private readonly EditorAssetFileSnapshot _before;
	private readonly EditorAssetFileSnapshot _after;

	public MaterialAssetEditUndoRedoEntry(string description, EditorAssetFileSnapshot before, EditorAssetFileSnapshot after)
	{
		Description = string.IsNullOrWhiteSpace(description) ? "Edit Material Asset" : description;
		_before = before;
		_after = after;
	}

	public string Description { get; }

	public void Undo(EditorUndoRedoContext context)
	{
		context.AssetSnapshotService.ApplyMaterialAssetSnapshot(_before);
	}

	public void Redo(EditorUndoRedoContext context)
	{
		context.AssetSnapshotService.ApplyMaterialAssetSnapshot(_after);
	}
}

public sealed class DataAssetEditUndoRedoEntry : IEditorUndoRedoEntry
{
	private readonly EditorAssetFileSnapshot _before;
	private readonly EditorAssetFileSnapshot _after;

	public DataAssetEditUndoRedoEntry(string description, EditorAssetFileSnapshot before, EditorAssetFileSnapshot after)
	{
		Description = string.IsNullOrWhiteSpace(description) ? "Edit Data Asset" : description;
		_before = before;
		_after = after;
	}

	public string Description { get; }

	public void Undo(EditorUndoRedoContext context)
	{
		context.AssetSnapshotService.ApplyDataAssetSnapshot(_before);
	}

	public void Redo(EditorUndoRedoContext context)
	{
		context.AssetSnapshotService.ApplyDataAssetSnapshot(_after);
	}
}

public sealed class EditorUndoRedoService : IEditorUndoRedoService
{
	private readonly EditorUndoRedoContext _context;
	private readonly Stack<IEditorUndoRedoEntry> _undoStack = new();
	private readonly Stack<IEditorUndoRedoEntry> _redoStack = new();
	private string? _pendingDescription;
	private bool _isReplaying;

	public EditorUndoRedoService(
		IEditorSceneWorkspace sceneWorkspace,
		IEditorInteractionState interactionState,
		IEditorSceneSnapshotService sceneSnapshotService,
		IEditorAssetSnapshotService assetSnapshotService)
	{
		_context = new EditorUndoRedoContext
		{
			SceneWorkspace = sceneWorkspace ?? throw new ArgumentNullException(nameof(sceneWorkspace)),
			InteractionState = interactionState ?? throw new ArgumentNullException(nameof(interactionState)),
			SceneSnapshotService = sceneSnapshotService ?? throw new ArgumentNullException(nameof(sceneSnapshotService)),
			AssetSnapshotService = assetSnapshotService ?? throw new ArgumentNullException(nameof(assetSnapshotService))
		};
	}

	public bool CanUndo => _undoStack.Count > 0;
	public bool CanRedo => _redoStack.Count > 0;

	public void BeginCapture(string description)
	{
		if (_isReplaying)
		{
			return;
		}

		_pendingDescription = string.IsNullOrWhiteSpace(description) ? "Edit" : description;
	}

	public bool CommitCapture(IEditorUndoRedoEntry entry)
	{
		if (_isReplaying || entry is null)
		{
			_pendingDescription = null;
			return false;
		}

		_pendingDescription = null;
		_undoStack.Push(entry);
		_redoStack.Clear();
		return true;
	}

	public void CancelCapture()
	{
		_pendingDescription = null;
	}

	public bool Undo()
	{
		if (_undoStack.Count == 0)
		{
			return false;
		}

		var entry = _undoStack.Pop();
		_isReplaying = true;
		try
		{
			entry.Undo(_context);
		}
		finally
		{
			_isReplaying = false;
		}

		_redoStack.Push(entry);
		_pendingDescription = null;
		return true;
	}

	public bool Redo()
	{
		if (_redoStack.Count == 0)
		{
			return false;
		}

		var entry = _redoStack.Pop();
		_isReplaying = true;
		try
		{
			entry.Redo(_context);
		}
		finally
		{
			_isReplaying = false;
		}

		_undoStack.Push(entry);
		_pendingDescription = null;
		return true;
	}

	public void Clear()
	{
		_undoStack.Clear();
		_redoStack.Clear();
		_pendingDescription = null;
	}
}
