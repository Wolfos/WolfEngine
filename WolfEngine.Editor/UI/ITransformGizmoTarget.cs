using System.Numerics;

namespace WolfEngine.Editor.UI;

/// <summary>A target's transform when a drag starts. Rotation is normalised.</summary>
public readonly record struct TransformGizmoTargetState(Vector3 WorldPosition, Quaternion WorldRotation, Vector3 LocalScale);

/// <summary>
/// Whatever a <see cref="TransformGizmo"/> moves: scene entities, skeleton bones, or anything else
/// with a transform. The gizmo works purely in world space; converting to the target's own local
/// space, recording undo and marking things dirty are the target's job.
/// <para>
/// The gizmo keeps the instance it started a drag with until the drag ends, so a target should
/// describe a fixed set of transforms rather than follow a live selection.
/// </para>
/// </summary>
public interface ITransformGizmoTarget
{
	int Count { get; }

	/// <summary>Index 0 is the primary target: the gizmo's axes follow its rotation in local space.</summary>
	TransformGizmoTargetState GetState(int index);

	/// <summary>Called once when a drag starts, before any transform changes.</summary>
	void BeginEdit();

	void SetWorldPosition(int index, Vector3 worldPosition);
	void SetWorldRotation(int index, Quaternion worldRotation);
	void SetLocalScale(int index, Vector3 localScale);

	/// <summary>Called once after each drag update that changed transforms.</summary>
	void OnTransformsChanged();

	/// <summary>
	/// Called once when a drag ends. <paramref name="commit"/> is false when the drag was abandoned
	/// rather than released, for example because the viewport closed or the selection vanished.
	/// </summary>
	void EndEdit(bool commit);
}
