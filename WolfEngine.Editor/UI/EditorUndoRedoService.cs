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

/// <summary>
/// What an undo entry replays against. Authoring-scene entries always resolve their target through
/// <see cref="IEditorSceneWorkspace.CurrentScene"/>, so they are only valid for edits made to the
/// authoring scene, never for edits made to the isolated play-mode runtime scene.
/// </summary>
public enum EditorUndoRedoScope
{
	AuthoringScene,
	Asset
}

public interface IEditorUndoRedoEntry
{
	string Description { get; }
	EditorUndoRedoScope Scope { get; }
	void Undo(EditorUndoRedoContext context);
	void Redo(EditorUndoRedoContext context);
}

public sealed class EditorUndoRedoContext
{
	public required IEditorSceneWorkspace SceneWorkspace { get; init; }
	public required IEditorInteractionState InteractionState { get; init; }
	public required IEditorSceneSnapshotService SceneSnapshotService { get; init; }
	public required IEditorAssetSnapshotService AssetSnapshotService { get; init; }
	public required ITerrainAssetPersistenceService TerrainAssetPersistenceService { get; init; }
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

	public EditorUndoRedoScope Scope => EditorUndoRedoScope.AuthoringScene;

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

public sealed class SceneComponentRemovalUndoRedoEntry : IEditorUndoRedoEntry
{
	private readonly IReadOnlyList<SceneComponentSnapshot> _removedSnapshots;

	public SceneComponentRemovalUndoRedoEntry(string description, IReadOnlyList<SceneComponentSnapshot> removedSnapshots)
	{
		Description = string.IsNullOrWhiteSpace(description) ? "Remove Component" : description;
		_removedSnapshots = removedSnapshots ?? throw new ArgumentNullException(nameof(removedSnapshots));
	}

	public string Description { get; }

	public EditorUndoRedoScope Scope => EditorUndoRedoScope.AuthoringScene;

	public void Undo(EditorUndoRedoContext context)
	{
		context.SceneSnapshotService.ApplyComponentSnapshots(context.SceneWorkspace.CurrentScene, _removedSnapshots);
		EditorGui.RefreshSelectedEntity(context.SceneWorkspace.CurrentScene.World, requestFocus: false);
		context.InteractionState.MarkSceneDirty();
	}

	public void Redo(EditorUndoRedoContext context)
	{
		context.SceneSnapshotService.RemoveComponents(context.SceneWorkspace.CurrentScene, _removedSnapshots);
		EditorGui.RefreshSelectedEntity(context.SceneWorkspace.CurrentScene.World, requestFocus: false);
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

	public EditorUndoRedoScope Scope => EditorUndoRedoScope.AuthoringScene;

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

public sealed class EntityCreationUndoRedoEntry : IEditorUndoRedoEntry
{
	private readonly IReadOnlyList<DeletedEntitySnapshot> _createdEntities;

	public EntityCreationUndoRedoEntry(string description, IReadOnlyList<DeletedEntitySnapshot> createdEntities)
	{
		Description = string.IsNullOrWhiteSpace(description) ? "Duplicate Entity" : description;
		_createdEntities = createdEntities ?? throw new ArgumentNullException(nameof(createdEntities));
	}

	public string Description { get; }

	public EditorUndoRedoScope Scope => EditorUndoRedoScope.AuthoringScene;

	public void Undo(EditorUndoRedoContext context)
	{
		context.SceneSnapshotService.DeleteEntitiesByPersistentIds(context.SceneWorkspace.CurrentScene, GetCreatedEntityIds());
		context.InteractionState.MarkSceneDirty();
	}

	public void Redo(EditorUndoRedoContext context)
	{
		context.SceneSnapshotService.RestoreDeletedEntities(context.SceneWorkspace.CurrentScene, _createdEntities);
		context.InteractionState.MarkSceneDirty();
	}

	private IReadOnlyList<Guid> GetCreatedEntityIds()
	{
		var ids = new List<Guid>(_createdEntities.Count);
		for (var i = 0; i < _createdEntities.Count; i++)
		{
			ids.Add(_createdEntities[i].Entity.EntityId);
		}

		return ids;
	}
}

public sealed class EntityHierarchyUndoRedoEntry : IEditorUndoRedoEntry
{
	private readonly EntityHierarchySnapshot _before;
	private readonly EntityHierarchySnapshot _after;

	public EntityHierarchyUndoRedoEntry(string description, EntityHierarchySnapshot before, EntityHierarchySnapshot after)
	{
		Description = string.IsNullOrWhiteSpace(description) ? "Edit Hierarchy" : description;
		_before = before;
		_after = after;
	}

	public string Description { get; }

	public EditorUndoRedoScope Scope => EditorUndoRedoScope.AuthoringScene;

	public void Undo(EditorUndoRedoContext context)
	{
		EntityHierarchyEditorOperations.ApplySnapshot(context.SceneWorkspace.CurrentScene, _before);
		context.InteractionState.MarkSceneDirty();
	}

	public void Redo(EditorUndoRedoContext context)
	{
		EntityHierarchyEditorOperations.ApplySnapshot(context.SceneWorkspace.CurrentScene, _after);
		context.InteractionState.MarkSceneDirty();
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

	public EditorUndoRedoScope Scope => EditorUndoRedoScope.Asset;

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

	public EditorUndoRedoScope Scope => EditorUndoRedoScope.Asset;

	public void Undo(EditorUndoRedoContext context)
	{
		context.AssetSnapshotService.ApplyDataAssetSnapshot(_before);
	}

	public void Redo(EditorUndoRedoContext context)
	{
		context.AssetSnapshotService.ApplyDataAssetSnapshot(_after);
	}
}

public sealed class TerrainAssetEditUndoRedoEntry : IEditorUndoRedoEntry
{
	private readonly IReadOnlyList<TerrainAssetSnapshot> _before;
	private readonly IReadOnlyList<TerrainAssetSnapshot> _after;

	public TerrainAssetEditUndoRedoEntry(
		string description,
		IReadOnlyList<TerrainAssetSnapshot> before,
		IReadOnlyList<TerrainAssetSnapshot> after)
	{
		Description = string.IsNullOrWhiteSpace(description) ? "Terrain Stroke" : description;
		_before = before ?? throw new ArgumentNullException(nameof(before));
		_after = after ?? throw new ArgumentNullException(nameof(after));
	}

	public string Description { get; }

	public EditorUndoRedoScope Scope => EditorUndoRedoScope.Asset;

	public void Undo(EditorUndoRedoContext context)
	{
		context.TerrainAssetPersistenceService.ApplyTerrainAssetStates(_before);
		context.InteractionState.MarkSceneDirty();
	}

	public void Redo(EditorUndoRedoContext context)
	{
		context.TerrainAssetPersistenceService.ApplyTerrainAssetStates(_after);
		context.InteractionState.MarkSceneDirty();
	}
}

public sealed class EditorUndoRedoService : IEditorUndoRedoService
{
	private readonly EditorUndoRedoContext _context;
	private readonly IEditorPlaySession _playSession;
	private readonly Stack<IEditorUndoRedoEntry> _undoStack = new();
	private readonly Stack<IEditorUndoRedoEntry> _redoStack = new();
	private string? _pendingDescription;
	private bool _isReplaying;

	public EditorUndoRedoService(
		IEditorSceneWorkspace sceneWorkspace,
		IEditorInteractionState interactionState,
		IEditorSceneSnapshotService sceneSnapshotService,
		IEditorAssetSnapshotService assetSnapshotService,
		ITerrainAssetPersistenceService terrainAssetPersistenceService,
		IEditorPlaySession playSession)
	{
		_context = new EditorUndoRedoContext
		{
			SceneWorkspace = sceneWorkspace ?? throw new ArgumentNullException(nameof(sceneWorkspace)),
			InteractionState = interactionState ?? throw new ArgumentNullException(nameof(interactionState)),
			SceneSnapshotService = sceneSnapshotService ?? throw new ArgumentNullException(nameof(sceneSnapshotService)),
			AssetSnapshotService = assetSnapshotService ?? throw new ArgumentNullException(nameof(assetSnapshotService)),
			TerrainAssetPersistenceService = terrainAssetPersistenceService ?? throw new ArgumentNullException(nameof(terrainAssetPersistenceService))
		};
		_playSession = playSession ?? throw new ArgumentNullException(nameof(playSession));
	}

	public bool CanUndo => CanReplay(_undoStack);
	public bool CanRedo => CanReplay(_redoStack);

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
		if (_isReplaying || entry is null || IsBlockedByPlayMode(entry))
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
		if (CanReplay(_undoStack) == false)
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
		if (CanReplay(_redoStack) == false)
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

	/// <summary>
	/// Play mode edits the isolated runtime scene while the authoring scene stays frozen, so authoring-scene
	/// entries must neither be recorded nor replayed until play mode stops. Asset entries stay available
	/// because they replay against files rather than the open scene.
	/// </summary>
	private bool IsBlockedByPlayMode(IEditorUndoRedoEntry entry)
		=> entry.Scope == EditorUndoRedoScope.AuthoringScene && _playSession.IsActive;

	private bool CanReplay(Stack<IEditorUndoRedoEntry> stack)
		=> stack.Count > 0 && IsBlockedByPlayMode(stack.Peek()) == false;
}
