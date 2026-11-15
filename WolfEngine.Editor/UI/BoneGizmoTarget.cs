using System.Numerics;
using WolfEngine.Animation;

namespace WolfEngine.Editor.UI;

/// <summary>Where a bone sits in the world, and how to turn a world transform back into its local one.</summary>
internal interface IBoneSpace
{
	Matrix4x4 GetBoneWorldMatrix(int boneIndex);
	Matrix4x4 GetBoneParentWorldMatrix(int boneIndex);
}

/// <summary>Where a bone's edits go: its current local transform, and the sink for a new one.</summary>
internal interface IBoneEditSink
{
	bool TryGetBoneLocal(string bone, out BoneTransform local);
	bool SetBoneLocalTransform(string bone, BoneTransform local);
	IDisposable BeginEdit(string description);
}

/// <summary>
/// Lets the transform gizmo move one skeleton bone. The gizmo works in world space; this converts
/// each change into the bone's parent space, so the result is a forward-kinematics local transform.
/// A whole drag is one undo step.
/// </summary>
internal sealed class BoneGizmoTarget : ITransformGizmoTarget
{
	private readonly IBoneSpace _space;
	private readonly IBoneEditSink _sink;
	private readonly string _bone;
	private readonly int _boneIndex;
	private IDisposable? _edit;

	public BoneGizmoTarget(IBoneSpace space, IBoneEditSink sink, string bone, int boneIndex)
	{
		_space = space;
		_sink = sink;
		_bone = bone;
		_boneIndex = boneIndex;
	}

	public int Count => 1;

	public TransformGizmoTargetState GetState(int index)
	{
		var world = _space.GetBoneWorldMatrix(_boneIndex);
		Matrix4x4.Decompose(world, out _, out var worldRotation, out _);
		var localScale = _sink.TryGetBoneLocal(_bone, out var local) ? local.Scale : Vector3.One;
		return new TransformGizmoTargetState(
			world.Translation,
			TransformGizmo.NormalizeOrIdentity(worldRotation),
			localScale);
	}

	public void BeginEdit()
	{
		_edit ??= _sink.BeginEdit("Transform Bone");
	}

	public void SetWorldPosition(int index, Vector3 worldPosition)
	{
		if (Matrix4x4.Invert(_space.GetBoneParentWorldMatrix(_boneIndex), out var worldToParent) == false ||
		    _sink.TryGetBoneLocal(_bone, out var local) == false)
		{
			return;
		}

		local.Position = Vector3.Transform(worldPosition, worldToParent);
		_sink.SetBoneLocalTransform(_bone, local);
	}

	public void SetWorldRotation(int index, Quaternion worldRotation)
	{
		if (_sink.TryGetBoneLocal(_bone, out var local) == false)
		{
			return;
		}

		// A bone's world rotation is its local rotation followed by its parent's, so undo the parent's.
		Matrix4x4.Decompose(_space.GetBoneParentWorldMatrix(_boneIndex), out _, out var parentRotation, out _);
		parentRotation = TransformGizmo.NormalizeOrIdentity(parentRotation);
		local.Rotation = TransformGizmo.NormalizeOrIdentity(Quaternion.Inverse(parentRotation) * worldRotation);
		_sink.SetBoneLocalTransform(_bone, local);
	}

	public void SetLocalScale(int index, Vector3 localScale)
	{
		if (_sink.TryGetBoneLocal(_bone, out var local) == false)
		{
			return;
		}

		local.Scale = localScale;
		_sink.SetBoneLocalTransform(_bone, local);
	}

	public void OnTransformsChanged()
	{
	}

	public void EndEdit(bool commit)
	{
		_edit?.Dispose();
		_edit = null;
	}
}
