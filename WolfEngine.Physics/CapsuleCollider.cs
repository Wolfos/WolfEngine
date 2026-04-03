using System.Numerics;
using WolfEngine.ECS;

namespace WolfEngine.Physics;

public struct CapsuleCollider : IEntityComponent
{
	public float Radius;
	public float HalfHeight;
	public Vector3 Center;

	public static CapsuleCollider CreateDefault()
	{
		return new CapsuleCollider
		{
			Radius = 0.5f,
			HalfHeight = 0.5f,
			Center = Vector3.Zero
		};
	}
}
