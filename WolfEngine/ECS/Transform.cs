using System.Numerics;

namespace WolfEngine.ECS;

public struct Transform: IEntityComponent
{
	public Vector3 LocalPosition;
	public Quaternion LocalRotation;
	public Vector3 LocalScale;

	public Transform()
	{
		LocalPosition = default;
		LocalRotation = default;
		LocalScale = Vector3.One;
	}

	public Transform(Vector3 localPosition, Quaternion localRotation, Vector3 localScale)
	{
		LocalPosition = localPosition;
		LocalRotation = localRotation;
		LocalScale = localScale;
	}

	public Transform(Matrix4x4 fromTransform)
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