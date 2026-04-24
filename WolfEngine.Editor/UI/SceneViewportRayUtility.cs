using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public readonly struct SceneViewportRay
{
	public SceneViewportRay(Vector3 origin, Vector3 direction)
	{
		Origin = origin;
		Direction = direction;
	}

	public Vector3 Origin { get; }
	public Vector3 Direction { get; }
}

public static class SceneViewportRayUtility
{
	public static bool TryBuildInverseViewProjection(
		in Camera camera,
		in WorldTransform cameraWorldTransform,
		out Matrix4x4 inverseViewProjection)
	{
		inverseViewProjection = Matrix4x4.Identity;
		if (camera.ScreenResolution.X <= 0 ||
		    camera.ScreenResolution.Y <= 0 ||
		    Matrix4x4.Invert(cameraWorldTransform.LocalToWorld, out var view) == false)
		{
			return false;
		}

		var viewProjection = view * camera.Perspective;
		return Matrix4x4.Invert(viewProjection, out inverseViewProjection);
	}

	public static bool TryBuildWorldRay(
		in SceneViewportUiState viewportState,
		Vector2 mousePosition,
		Matrix4x4 inverseViewProjection,
		out SceneViewportRay ray)
	{
		ray = default;
		if (IsPointInsideRect(mousePosition, viewportState.ImageMin, viewportState.ImageMax) == false)
		{
			return false;
		}

		var width = viewportState.ImageMax.X - viewportState.ImageMin.X;
		var height = viewportState.ImageMax.Y - viewportState.ImageMin.Y;
		if (width <= 1e-5f || height <= 1e-5f)
		{
			return false;
		}

		var u = (mousePosition.X - viewportState.ImageMin.X) / width;
		var v = (mousePosition.Y - viewportState.ImageMin.Y) / height;
		var ndcX = (u * 2.0f) - 1.0f;
		var ndcY = 1.0f - (v * 2.0f);
		var nearPoint = UnprojectPoint(new Vector3(ndcX, ndcY, 0.0f), inverseViewProjection);
		var farPoint = UnprojectPoint(new Vector3(ndcX, ndcY, 1.0f), inverseViewProjection);
		if (nearPoint.HasValue == false || farPoint.HasValue == false)
		{
			return false;
		}

		var direction = farPoint.Value - nearPoint.Value;
		if (direction.LengthSquared() <= 1e-8f)
		{
			return false;
		}

		ray = new SceneViewportRay(nearPoint.Value, Vector3.Normalize(direction));
		return true;
	}

	private static Vector3? UnprojectPoint(Vector3 ndc, Matrix4x4 inverseViewProjection)
	{
		var clip = new Vector4(ndc, 1.0f);
		var world = Vector4.Transform(clip, inverseViewProjection);
		if (MathF.Abs(world.W) <= 1e-6f)
		{
			return null;
		}

		return new Vector3(world.X / world.W, world.Y / world.W, world.Z / world.W);
	}

	private static bool IsPointInsideRect(Vector2 point, Vector2 min, Vector2 max)
	{
		return point.X >= min.X &&
		       point.X <= max.X &&
		       point.Y >= min.Y &&
		       point.Y <= max.Y;
	}
}
