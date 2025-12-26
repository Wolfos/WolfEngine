using System.Numerics;

namespace WolfEngine.ECS;

public struct LocalTransform: IEntityComponent
{
	internal Vector3 LocalPosition;
	internal Quaternion LocalRotation;
	internal Vector3 LocalScale;

	internal bool IsDirty;

	public LocalTransform(Vector3 localPosition, Quaternion localRotation, Vector3 localScale)
	{
		LocalPosition = localPosition;
		LocalRotation = localRotation;
		LocalScale = localScale;
	}

	public LocalTransform(Matrix4x4 fromTransform)
	{
		Matrix4x4.Decompose(fromTransform, out LocalScale, out LocalRotation, out LocalPosition);
	}

	public Matrix4x4 GetTransform()
	{
		var scale = Matrix4x4.CreateScale(LocalScale);
		var rotation = Matrix4x4.CreateFromQuaternion(LocalRotation);
		var translation = Matrix4x4.CreateTranslation(LocalPosition);

		return scale * rotation * translation;
	}
}

