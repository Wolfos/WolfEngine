using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Editor.UI;
using WolfEngine.Physics;
using WolfEngine.Rendering;

namespace WolfEngine.Editor;

public sealed class BoxColliderGizmoDrawer : IOnDrawGizmos
{
	private static readonly ColorRGBA ColliderColor = new(0.2f, 0.8f, 1.0f, 1.0f);

	private readonly IGizmoLineRenderer _lineRenderer;

	public BoxColliderGizmoDrawer(IGizmoLineRenderer lineRenderer)
	{
		_lineRenderer = lineRenderer ?? throw new ArgumentNullException(nameof(lineRenderer));
	}

	public void OnDrawGizmos(World world)
	{
		if (EditorGui.HasSelectedEntity == false)
		{
			return;
		}

		foreach (var entry in world.View<BoxCollider>())
		{
			if (entry.Entity != EditorGui.SelectedEntity ||
			    world.HasComponent<WorldTransform>(entry.Entity) == false)
			{
				continue;
			}

			DrawBox(world.GetComponent<WorldTransform>(entry.Entity).LocalToWorld, entry.First);
		}
	}

	public WorldTag GetTag() => WorldTag.Authoring;

	private void DrawBox(Matrix4x4 localToWorld, in BoxCollider collider)
	{
		var min = collider.Center - collider.HalfExtents;
		var max = collider.Center + collider.HalfExtents;
		Span<Vector3> corners =
		[
			new(min.X, min.Y, min.Z),
			new(max.X, min.Y, min.Z),
			new(max.X, min.Y, max.Z),
			new(min.X, min.Y, max.Z),
			new(min.X, max.Y, min.Z),
			new(max.X, max.Y, min.Z),
			new(max.X, max.Y, max.Z),
			new(min.X, max.Y, max.Z)
		];

		for (var i = 0; i < corners.Length; i++)
		{
			corners[i] = Vector3.Transform(corners[i], localToWorld);
		}

		DrawEdge(corners[0], corners[1]);
		DrawEdge(corners[1], corners[2]);
		DrawEdge(corners[2], corners[3]);
		DrawEdge(corners[3], corners[0]);
		DrawEdge(corners[4], corners[5]);
		DrawEdge(corners[5], corners[6]);
		DrawEdge(corners[6], corners[7]);
		DrawEdge(corners[7], corners[4]);
		DrawEdge(corners[0], corners[4]);
		DrawEdge(corners[1], corners[5]);
		DrawEdge(corners[2], corners[6]);
		DrawEdge(corners[3], corners[7]);
	}

	private void DrawEdge(Vector3 startWorld, Vector3 endWorld)
	{
		_lineRenderer.DrawLine(startWorld, endWorld, ColliderColor);
	}
}
