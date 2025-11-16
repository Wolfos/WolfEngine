using System.Numerics;

namespace WolfEngine.ECS;

public struct Transform: IEntityComponent
{
	public Vector3 LocalPosition;
	public Quaternion LocalRotation;
	public Vector3 LocalScale;
}