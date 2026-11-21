using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Editor.UI;
using WolfEngine.Rendering;

namespace WolfEngine.Editor;

/// <summary>
/// Shows the view frustum of a selected camera, from its near plane out to a far end that is capped so a
/// camera with a far plane in the thousands of metres still draws something readable.
/// </summary>
public sealed class CameraGizmoDrawer : IOnDrawGizmos
{
	/// <summary>The farthest distance the frustum is drawn to, in world units.</summary>
	public const float MaxDrawnFarDistance = 10.0f;

	private const float DefaultFovDegrees = 70.0f;
	private static readonly ColorRGBA FrustumColor = new(0.85f, 0.85f, 0.85f, 1.0f);

	private readonly IGizmoLineRenderer _lineRenderer;

	public CameraGizmoDrawer(IGizmoLineRenderer lineRenderer)
	{
		_lineRenderer = lineRenderer ?? throw new ArgumentNullException(nameof(lineRenderer));
	}

	public void OnDrawGizmos(World world)
	{
		if (EditorGui.SelectedEntities.Count == 0)
		{
			return;
		}

		foreach (var entry in world.View<Camera>())
		{
			if (EditorGui.SelectedEntities.Contains(entry.Entity) == false ||
			    world.HasComponent<WorldTransform>(entry.Entity) == false)
			{
				continue;
			}

			DrawFrustum(
				GizmoShapes.WithoutScale(world.GetComponent<WorldTransform>(entry.Entity).LocalToWorld),
				entry.First);
		}
	}

	public WorldTag GetTag() => WorldTag.Authoring;

	private void DrawFrustum(Matrix4x4 localToWorld, in Camera camera)
	{
		var fovDegrees = camera.Fov < 1.0f ? DefaultFovDegrees : camera.Fov;
		if (float.IsFinite(fovDegrees) == false || fovDegrees >= 179.0f)
		{
			return;
		}

		var nearDistance = camera.NearPlane > 0.0f ? camera.NearPlane : Camera.DefaultNearPlane;
		var farDistance = camera.FarPlane > nearDistance ? camera.FarPlane : Camera.DefaultFarPlane;
		farDistance = MathF.Min(farDistance, MaxDrawnFarDistance);
		if (farDistance <= nearDistance)
		{
			return;
		}

		var aspect = camera.ScreenResolution.X > 0 && camera.ScreenResolution.Y > 0
			? camera.ScreenResolution.X / (float)camera.ScreenResolution.Y
			: 1.0f;
		var tanHalfFov = MathF.Tan(float.DegreesToRadians(fovDegrees) * 0.5f);

		Span<Vector3> nearCorners = stackalloc Vector3[4];
		Span<Vector3> farCorners = stackalloc Vector3[4];
		FillCorners(nearCorners, localToWorld, nearDistance, tanHalfFov, aspect);
		FillCorners(farCorners, localToWorld, farDistance, tanHalfFov, aspect);

		for (var corner = 0; corner < 4; corner++)
		{
			var next = (corner + 1) % 4;
			_lineRenderer.DrawLine(nearCorners[corner], nearCorners[next], FrustumColor);
			_lineRenderer.DrawLine(farCorners[corner], farCorners[next], FrustumColor);
			_lineRenderer.DrawLine(nearCorners[corner], farCorners[corner], FrustumColor);
		}
	}

	private static void FillCorners(
		Span<Vector3> corners,
		Matrix4x4 localToWorld,
		float distance,
		float tanHalfFov,
		float aspect)
	{
		var halfHeight = tanHalfFov * distance;
		var halfWidth = halfHeight * aspect;
		corners[0] = Vector3.Transform(new Vector3(-halfWidth, halfHeight, distance), localToWorld);
		corners[1] = Vector3.Transform(new Vector3(halfWidth, halfHeight, distance), localToWorld);
		corners[2] = Vector3.Transform(new Vector3(halfWidth, -halfHeight, distance), localToWorld);
		corners[3] = Vector3.Transform(new Vector3(-halfWidth, -halfHeight, distance), localToWorld);
	}
}
