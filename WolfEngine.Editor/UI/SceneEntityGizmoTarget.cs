using System.Numerics;
using WolfEngine.ECS;

namespace WolfEngine.Editor.UI;

/// <summary>
/// Adapts a scene-authoring transform edit to the gizmo. A drag is one transform edit, so it becomes one
/// "Transform" undo step on the authoring scene, and only when something actually changed.
/// </summary>
internal sealed class SceneEntityGizmoTarget : ITransformGizmoTarget
{
	private readonly EditorScene _scene;
	private readonly IReadOnlyList<Entity> _entities;
	private readonly ISceneAuthoringService _sceneAuthoring;
	private ISceneTransformEdit? _edit;

	public SceneEntityGizmoTarget(
		EditorScene scene,
		IReadOnlyList<Entity> entities,
		ISceneAuthoringService sceneAuthoring)
	{
		_scene = scene ?? throw new ArgumentNullException(nameof(scene));
		_entities = entities ?? throw new ArgumentNullException(nameof(entities));
		_sceneAuthoring = sceneAuthoring ?? throw new ArgumentNullException(nameof(sceneAuthoring));
	}

	public int Count => _entities.Count;

	public TransformGizmoTargetState GetState(int index)
	{
		var world = _scene.World;
		var entity = _entities[index];
		ref var worldTransform = ref world.GetComponent<WorldTransform>(entity);
		ref var localTransform = ref world.GetComponent<LocalTransform>(entity);
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
		_edit = _sceneAuthoring.BeginTransformEdit(_scene, _entities);
	}

	public void SetWorldPosition(int index, Vector3 worldPosition)
	{
		_edit?.SetWorldPosition(_entities[index], worldPosition);
	}

	public void SetWorldRotation(int index, Quaternion worldRotation)
	{
		_edit?.SetWorldRotation(_entities[index], worldRotation);
	}

	public void SetLocalScale(int index, Vector3 localScale)
	{
		_edit?.SetLocalScale(_entities[index], localScale);
	}

	public void OnTransformsChanged()
	{
	}

	/// <summary>
	/// An abandoned drag still commits: the entities stay where the drag left them, so that move must be
	/// undoable rather than silently unrecorded.
	/// </summary>
	public void EndEdit(bool commit)
	{
		var edit = _edit;
		_edit = null;
		edit?.Commit();
	}
}
