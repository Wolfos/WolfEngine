using System.Numerics;
using WolfEngine.Animation;
using WolfEngine.ECS;
using WolfEngine.Mathematics;

namespace WolfEngine.Editor.UI;

/// <summary>An entity hit by a viewport pick, with the world-space hit point and distance along the ray.</summary>
public readonly record struct ScenePickHit(Entity Entity, Vector3 Point, float Distance);

/// <summary>
/// Resolves which entity a scene-viewport ray hits, by intersecting the ray with renderer geometry
/// on the CPU.
/// </summary>
public static class SceneViewportPicker
{
	// Meshes with no thickness on an axis — ground planes, quads, decals — produce a degenerate slab
	// whose entry and exit distances are the same value. Padding the bounds keeps them from falling
	// out of the broad phase on rounding alone.
	private const float BoundsPaddingLocalUnits = 1e-4f;

	/// <summary>
	/// Finds the closest renderable entity along <paramref name="ray"/> within
	/// <paramref name="maxDistance"/> world units.
	/// </summary>
	public static bool TryPick(World world, in SceneViewportRay ray, float maxDistance, out ScenePickHit hit)
	{
		ArgumentNullException.ThrowIfNull(world);

		hit = default;
		var closest = maxDistance > 0.0f ? maxDistance : float.PositiveInfinity;
		var found = false;

		foreach (var entry in world.View<WorldTransform, MeshRenderer>())
		{
			ref var transform = ref entry.First;
			ref var meshRenderer = ref entry.Second;

			if (world.IsEnabled(entry.Entity) == false || meshRenderer.TryValidate() == false)
			{
				continue;
			}

			if (TryIntersectMesh(meshRenderer.Mesh, transform.LocalToWorld, ray, closest, out var distance))
			{
				closest = distance;
				hit = new ScenePickHit(entry.Entity, ray.Origin + (ray.Direction * distance), distance);
				found = true;
			}
		}

		foreach (var entry in world.View<WorldTransform, SkinnedMeshRenderer>())
		{
			ref var transform = ref entry.First;
			ref var skinnedRenderer = ref entry.Second;
			if (world.IsEnabled(entry.Entity) == false || skinnedRenderer.TryValidate() == false)
			{
				continue;
			}

			// Picked against the bind pose. The deformed vertices only exist in GPU memory
			if (TryIntersectMesh(skinnedRenderer.Mesh, transform.LocalToWorld, ray, closest, out var distance))
			{
				closest = distance;
				hit = new ScenePickHit(entry.Entity, ray.Origin + (ray.Direction * distance), distance);
				found = true;
			}
		}

		return found;
	}

	private static bool TryIntersectMesh(
		Mesh mesh,
		in Matrix4x4 localToWorld,
		in SceneViewportRay ray,
		float maxDistance,
		out float distance)
	{
		distance = 0.0f;
		if (Matrix4x4.Invert(localToWorld, out var worldToLocal) == false)
		{
			return false;
		}

		var localOrigin = Vector3.Transform(ray.Origin, worldToLocal);
		var unscaledDirection = Vector3.TransformNormal(ray.Direction, worldToLocal);
		var directionLength = unscaledDirection.Length();
		if (directionLength <= 1e-12f || float.IsFinite(directionLength) == false)
		{
			return false;
		}
		
		var localDirection = unscaledDirection / directionLength;
		var maxLocalDistance = maxDistance * directionLength;
		if (float.IsFinite(maxLocalDistance) == false)
		{
			maxLocalDistance = float.MaxValue;
		}

		if (TryIntersectBounds(mesh.BoundingBox, localOrigin, localDirection, maxLocalDistance) == false)
		{
			return false;
		}

		var vertices = mesh.Vertices;
		var indices = mesh.Indices;
		var closestLocal = maxLocalDistance;
		var found = false;
		for (var i = 0; i + 2 < indices.Length; i += 3)
		{
			var index0 = indices[i];
			var index1 = indices[i + 1];
			var index2 = indices[i + 2];
			if (index0 >= (uint)vertices.Length || index1 >= (uint)vertices.Length || index2 >= (uint)vertices.Length)
			{
				continue;
			}

			// Double sided on purpose
			if (TryIntersectTriangle(
				    ToVector3(vertices[index0]),
				    ToVector3(vertices[index1]),
				    ToVector3(vertices[index2]),
				    localOrigin,
				    localDirection,
				    out var localDistance) == false)
			{
				continue;
			}

			if (localDistance >= closestLocal)
			{
				continue;
			}

			closestLocal = localDistance;
			found = true;
		}

		if (found == false)
		{
			return false;
		}

		distance = closestLocal / directionLength;
		return true;
	}

	private static bool TryIntersectBounds(in Box box, Vector3 origin, Vector3 direction, float maxDistance)
	{
		var halfExtents = box.HalfExtents + new Vector3(BoundsPaddingLocalUnits);
		var min = box.Center - halfExtents;
		var max = box.Center + halfExtents;
		var near = 0.0f;
		var far = maxDistance;

		for (var axis = 0; axis < 3; axis++)
		{
			var directionComponent = GetComponent(direction, axis);
			var originComponent = GetComponent(origin, axis);
			var minComponent = GetComponent(min, axis);
			var maxComponent = GetComponent(max, axis);
			if (MathF.Abs(directionComponent) <= 1e-9f)
			{
				if (originComponent < minComponent || originComponent > maxComponent)
				{
					return false;
				}

				continue;
			}

			var inverseDirection = 1.0f / directionComponent;
			var entry = (minComponent - originComponent) * inverseDirection;
			var exit = (maxComponent - originComponent) * inverseDirection;
			if (entry > exit)
			{
				(entry, exit) = (exit, entry);
			}

			near = MathF.Max(near, entry);
			far = MathF.Min(far, exit);
			if (near > far)
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>Möller–Trumbore intersection. <paramref name="direction"/> must be normalised.</summary>
	private static bool TryIntersectTriangle(
		Vector3 vertex0,
		Vector3 vertex1,
		Vector3 vertex2,
		Vector3 origin,
		Vector3 direction,
		out float distance)
	{
		const float epsilon = 1e-9f;

		distance = 0.0f;
		var edge1 = vertex1 - vertex0;
		var edge2 = vertex2 - vertex0;
		var pVector = Vector3.Cross(direction, edge2);
		var determinant = Vector3.Dot(edge1, pVector);
		if (MathF.Abs(determinant) <= epsilon)
		{
			return false;
		}

		var inverseDeterminant = 1.0f / determinant;
		var tVector = origin - vertex0;
		var u = Vector3.Dot(tVector, pVector) * inverseDeterminant;
		if (u < 0.0f || u > 1.0f)
		{
			return false;
		}

		var qVector = Vector3.Cross(tVector, edge1);
		var v = Vector3.Dot(direction, qVector) * inverseDeterminant;
		if (v < 0.0f || u + v > 1.0f)
		{
			return false;
		}

		distance = Vector3.Dot(edge2, qVector) * inverseDeterminant;
		return distance > 0.0f;
	}

	private static float GetComponent(Vector3 value, int axis)
	{
		return axis switch
		{
			0 => value.X,
			1 => value.Y,
			_ => value.Z
		};
	}

	private static Vector3 ToVector3(Vector4 value)
	{
		return new Vector3(value.X, value.Y, value.Z);
	}
}
