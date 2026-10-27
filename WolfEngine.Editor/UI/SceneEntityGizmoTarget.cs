using System.Numerics;
using WolfEngine.ECS;

namespace WolfEngine.Editor.UI;

/// <summary>
/// Moves scene entities. A drag is one "Transform" undo step built from LocalTransform
/// snapshots, and only when something actually changed.
/// </summary>
internal sealed class SceneEntityGizmoTarget : ITransformGizmoTarget
{
	private readonly EditorScene _scene;
	private readonly World _world;
	private readonly List<Entity> _entities;
	private readonly IEditorInteractionState _interactionState;
	private readonly IEditorSceneSnapshotService _sceneSnapshotService;
	private readonly IEditorUndoRedoService _undoRedoService;
	private readonly List<SceneComponentSnapshot> _beforeSnapshots = new();

	public SceneEntityGizmoTarget(
		EditorScene scene,
		World world,
		List<Entity> entities,
		IEditorInteractionState interactionState,
		IEditorSceneSnapshotService sceneSnapshotService,
		IEditorUndoRedoService undoRedoService)
	{
		_scene = scene;
		_world = world;
		_entities = entities;
		_interactionState = interactionState;
		_sceneSnapshotService = sceneSnapshotService;
		_undoRedoService = undoRedoService;
	}

	public int Count => _entities.Count;

	public TransformGizmoTargetState GetState(int index)
	{
		var entity = _entities[index];
		ref var worldTransform = ref _world.GetComponent<WorldTransform>(entity);
		ref var localTransform = ref _world.GetComponent<LocalTransform>(entity);
		if (Matrix4x4.Decompose(worldTransform.LocalToWorld, out _, out var worldRotation, out _) == false)
		{
			worldRotation = localTransform.LocalRotation;
		}

		return new TransformGizmoTargetState(
			worldTransform.LocalToWorld.Translation,
			TransformGizmo.NormalizeOrIdentity(worldRotation),
			localTransform.LocalScale);
	}

	public void BeginEdit()
	{
		_beforeSnapshots.Clear();
		foreach (var entity in _entities)
		{
			_beforeSnapshots.Add(_sceneSnapshotService.CaptureComponent(_scene, entity, typeof(LocalTransform)));
		}
	}

	public void SetWorldPosition(int index, Vector3 worldPosition)
	{
		if (_world.IsAlive(_entities[index]))
		{
			_world.SetWorldPosition(_entities[index], worldPosition);
		}
	}

	public void SetWorldRotation(int index, Quaternion worldRotation)
	{
		if (_world.IsAlive(_entities[index]))
		{
			_world.SetWorldRotation(_entities[index], worldRotation);
		}
	}

	public void SetLocalScale(int index, Vector3 localScale)
	{
		if (_world.IsAlive(_entities[index]))
		{
			_world.SetLocalScale(_entities[index], localScale);
		}
	}

	public void OnTransformsChanged()
	{
		_interactionState.MarkSceneDirty(_world);
	}

	public void EndEdit(bool commit)
	{
		if (commit)
		{
			CommitUndo();
		}

		_beforeSnapshots.Clear();
	}

	private void CommitUndo()
	{
		var afterSnapshots = new List<SceneComponentSnapshot>(_entities.Count);
		foreach (var entity in _entities)
		{
			if (_scene.World.IsAlive(entity))
			{
				afterSnapshots.Add(_sceneSnapshotService.CaptureComponent(_scene, entity, typeof(LocalTransform)));
			}
		}

		if (_beforeSnapshots.Count == 0 || afterSnapshots.Count != _beforeSnapshots.Count)
		{
			return;
		}

		var changed = false;
		for (var i = 0; i < afterSnapshots.Count; i++)
		{
			if (_beforeSnapshots[i].EntityId != afterSnapshots[i].EntityId ||
			    string.Equals(_beforeSnapshots[i].Data.GetRawText(), afterSnapshots[i].Data.GetRawText(), StringComparison.Ordinal) == false)
			{
				changed = true;
				break;
			}
		}

		if (changed == false)
		{
			return;
		}

		_undoRedoService.BeginCapture("Transform");
		_undoRedoService.CommitCapture(new SceneComponentEditUndoRedoEntry("Transform", _beforeSnapshots.ToArray(), afterSnapshots));
	}
}
