using System.Numerics;
using WolfEngine.ECS;

namespace WolfEngine.Physics;

public struct BoxCollider : IEntityComponent
{
	public Vector3 HalfExtents;
	public Vector3 Center;
	[NotSerialized]
	[HideFromEditor]
	internal bool PhysicsCacheValid;
	[NotSerialized]
	[HideFromEditor]
	internal Vector3 CachedHalfExtents;
	[NotSerialized]
	[HideFromEditor]
	internal Vector3 CachedCenter;
	[NotSerialized]
	[HideFromEditor]
	internal Vector3 CachedWorldScale;
	[NotSerialized]
	[HideFromEditor]
	internal Vector3 CachedScaledHalfExtents;
	[NotSerialized]
	[HideFromEditor]
	internal Vector3 CachedScaledCenter;
	[NotSerialized]
	[HideFromEditor]
	internal uint CachedLayer;
	[NotSerialized]
	[HideFromEditor]
	internal uint CachedCollidesWith;

	public static BoxCollider CreateDefault()
	{
		return new BoxCollider
		{
			HalfExtents = new Vector3(0.5f),
			Center = Vector3.Zero,
			PhysicsCacheValid = false
		};
	}

	public void ApplyDefaultValues(World world, Entity entity)
	{
		if (world.HasComponent<MeshRenderer>(entity) == false) return;

		var meshRenderer = world.GetComponent<MeshRenderer>(entity);
		if (meshRenderer.Mesh is null) return;

		HalfExtents = meshRenderer.Mesh.BoundingBox.HalfExtents;
		Center = meshRenderer.Mesh.BoundingBox.Center;
		PhysicsCacheValid = false;
	}
}
