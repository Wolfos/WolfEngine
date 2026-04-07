using System.Numerics;
using WolfEngine.ECS;

namespace WolfEngine.Physics;

public struct CapsuleCollider : IEntityComponent
{
	public float Radius;
	public float HalfHeight;
	public Vector3 Center;
	[NotSerialized]
	[HideFromEditor]
	internal bool PhysicsCacheValid;
	[NotSerialized]
	[HideFromEditor]
	internal float CachedRadius;
	[NotSerialized]
	[HideFromEditor]
	internal float CachedHalfHeight;
	[NotSerialized]
	[HideFromEditor]
	internal Vector3 CachedCenter;
	[NotSerialized]
	[HideFromEditor]
	internal Vector3 CachedWorldScale;
	[NotSerialized]
	[HideFromEditor]
	internal float CachedScaledRadius;
	[NotSerialized]
	[HideFromEditor]
	internal float CachedScaledHalfHeight;
	[NotSerialized]
	[HideFromEditor]
	internal Vector3 CachedScaledCenter;
	[NotSerialized]
	[HideFromEditor]
	internal uint CachedLayer;
	[NotSerialized]
	[HideFromEditor]
	internal uint CachedCollidesWith;

	public static CapsuleCollider CreateDefault()
	{
		return new CapsuleCollider
		{
			Radius = 0.5f,
			HalfHeight = 0.5f,
			Center = Vector3.Zero,
			PhysicsCacheValid = false
		};
	}
}
