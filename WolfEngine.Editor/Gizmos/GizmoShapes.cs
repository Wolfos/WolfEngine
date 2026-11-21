using System.Numerics;
using WolfEngine.Rendering;

namespace WolfEngine.Editor;

/// <summary>Wireframe shapes shared by the entity gizmo drawers.</summary>
internal static class GizmoShapes
{
	public const int CircleSegments = 32;

	/// <summary>
	/// Returns the transform with its scale removed, so a gizmo that describes a physical quantity such as
	/// a light range or a camera frustum does not stretch with the entity's scale.
	/// </summary>
	public static Matrix4x4 WithoutScale(Matrix4x4 localToWorld)
	{
		if (Matrix4x4.Decompose(localToWorld, out _, out var rotation, out var translation) == false)
		{
			return Matrix4x4.CreateTranslation(localToWorld.Translation);
		}

		var lengthSquared = rotation.LengthSquared();
		if (lengthSquared <= 1e-8f || float.IsFinite(lengthSquared) == false)
		{
			return Matrix4x4.CreateTranslation(translation);
		}

		return Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation)) *
		       Matrix4x4.CreateTranslation(translation);
	}

	public static void DrawCircle(
		IGizmoLineRenderer lineRenderer,
		Vector3 center,
		Vector3 axisA,
		Vector3 axisB,
		float radius,
		ColorRGBA color)
	{
		var previous = center + axisA * radius;
		for (var segment = 1; segment <= CircleSegments; segment++)
		{
			var angle = segment / (float)CircleSegments * MathF.Tau;
			var next = center + (axisA * MathF.Cos(angle) + axisB * MathF.Sin(angle)) * radius;
			lineRenderer.DrawLine(previous, next, color);
			previous = next;
		}
	}
}
