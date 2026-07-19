using System.Numerics;
using WolfEngine.ECS;

namespace WolfEngine.Physics;

/// <summary>Authoring shape for a sphere collider.</summary>
public struct SphereCollider : IEntityComponent
{
	public float Radius;
	public Vector3 Center;
	[NotSerialized]
	[HideFromEditor]
	internal bool PhysicsCacheValid;
	[NotSerialized]
	[HideFromEditor]
	internal float CachedRadius;
	[NotSerialized]
	[HideFromEditor]
	internal Vector3 CachedCenter;
	[NotSerialized]
	[HideFromEditor]
	internal float CachedScaledRadius;
	[NotSerialized]
	[HideFromEditor]
	internal Vector3 CachedScaledCenter;
	[NotSerialized]
	[HideFromEditor]
	internal uint CachedLayer;
	[NotSerialized]
	[HideFromEditor]
	internal uint CachedCollidesWith;

	public static SphereCollider CreateDefault()
	{
		return new SphereCollider
		{
			Radius = 0.5f,
			Center = Vector3.Zero,
			PhysicsCacheValid = false
		};
	}

	public void ApplyDefaultValues(World world, Entity entity)
	{
		Radius = 0.5f;
		Center = Vector3.Zero;
		PhysicsCacheValid = false;
	}
}
