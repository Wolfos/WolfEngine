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
	
	public void ApplyDefaultValues(World world, Entity entity)
	{
		Radius = 0.5f;
		HalfHeight = 0.5f; 
	}

	public static CapsuleCollider CreateDefault()
	{
		var cc = new CapsuleCollider();
		cc.ApplyDefaultValues(null!, new Entity());
		return cc;
	}
}
