using System.Numerics;

namespace WolfEngine.ECS;

public struct Transform: IEntityComponent
{
	public Vector3 LocalPosition { get; set; }
	public Quaternion LocalRotation { get; set; }
	public Vector3 LocalScale { get; set; }
}