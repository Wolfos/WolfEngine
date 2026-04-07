using System.Numerics;
using WolfEngine.ECS;

namespace WolfEngine.Physics;

public struct BoxCollider : IEntityComponent
{
	public Vector3 HalfExtents;
	public Vector3 Center;

	public void ApplyDefaultValues(World world, Entity entity)
	{
		if (world.HasComponent<MeshRenderer>(entity) == false) return;

		var meshRenderer = world.GetComponent<MeshRenderer>(entity);
		HalfExtents = meshRenderer.Mesh.BoundingBox.HalfExtents;
		Center = meshRenderer.Mesh.BoundingBox.Center;
	}
}
