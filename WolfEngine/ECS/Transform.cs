using System.Numerics;

namespace WolfEngine.ECS;

public struct Transform: IEntityComponent
{
	public Vector3 Position => LocalPosition; // TODO: World space conversion when parenting exists
	public Quaternion Rotation => LocalRotation; // TODO: World space conversion when parenting exists
	public Vector3 Scale => LocalScale; // TODO: World space conversion when parenting exists
	
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
		var scale = Matrix4x4.CreateScale(Scale);
		var rotation = Matrix4x4.CreateFromQuaternion(Rotation);
		var translation = Matrix4x4.CreateTranslation(Position);

		return scale * rotation * translation;
	}
}