using System.Numerics;
using WolfEngine.ECS;

namespace WolfEngine.Physics;

public struct BoxCollider : IEntityComponent
{
	public Vector3 HalfExtents;
	public Vector3 Center;

	public static BoxCollider CreateDefault()
	{
		return new BoxCollider
		{
			HalfExtents = new Vector3(0.5f),
			Center = Vector3.Zero
		};
	}
}
