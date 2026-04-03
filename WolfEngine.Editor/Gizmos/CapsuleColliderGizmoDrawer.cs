using System;
using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Editor.UI;
using WolfEngine.Physics;
using WolfEngine.Rendering;

namespace WolfEngine.Editor;

public sealed class CapsuleColliderGizmoDrawer : IOnDrawGizmos
{
	private const int RingSegments = 24;
	private const int ArcSegments = 16;
	private static readonly ColorRGBA ColliderColor = new(0.2f, 0.8f, 1.0f, 1.0f);

	private readonly IGizmoLineRenderer _lineRenderer;

	public CapsuleColliderGizmoDrawer(IGizmoLineRenderer lineRenderer)
	{
		_lineRenderer = lineRenderer ?? throw new ArgumentNullException(nameof(lineRenderer));
	}

	public void OnDrawGizmos(World world)
	{
		if (EditorGui.HasSelectedEntity == false)
		{
			return;
		}

		foreach (var entry in world.View<CapsuleCollider>())
		{
			if (entry.Entity != EditorGui.SelectedEntity ||
			    world.HasComponent<WorldTransform>(entry.Entity) == false)
			{
				continue;
			}

			DrawCapsule(world.GetComponent<WorldTransform>(entry.Entity).LocalToWorld, entry.First);
		}
	}

	public WorldTag GetTag() => WorldTag.Authoring;

	private void DrawCapsule(Matrix4x4 localToWorld, in CapsuleCollider collider)
	{
		var rotation = Quaternion.Identity;
		var scale = Vector3.One;
		if (Matrix4x4.Decompose(localToWorld, out var worldScale, out var worldRotation, out _) == false)
		{
			worldScale = Vector3.One;
			worldRotation = Quaternion.Identity;
		}

		scale = new Vector3(MathF.Abs(worldScale.X), MathF.Abs(worldScale.Y), MathF.Abs(worldScale.Z));
		rotation = NormalizeOrIdentity(worldRotation);

		var centerWorld = Vector3.Transform(collider.Center, localToWorld);
		var axisX = Vector3.Transform(Vector3.UnitX, rotation);
		var axisY = Vector3.Transform(Vector3.UnitY, rotation);
		var axisZ = Vector3.Transform(Vector3.UnitZ, rotation);
		var halfHeight = MathF.Max(0.001f, MathF.Abs(collider.HalfHeight) * scale.Y);
		var radiusScale = MathF.Max(scale.X, scale.Z);
		var radius = MathF.Max(0.001f, MathF.Abs(collider.Radius) * radiusScale);
		var topCenter = centerWorld + axisY * halfHeight;
		var bottomCenter = centerWorld - axisY * halfHeight;

		DrawCircle(topCenter, axisX, axisZ, radius);
		DrawCircle(bottomCenter, axisX, axisZ, radius);

		DrawSideLine(topCenter + axisX * radius, bottomCenter + axisX * radius);
		DrawSideLine(topCenter - axisX * radius, bottomCenter - axisX * radius);
		DrawSideLine(topCenter + axisZ * radius, bottomCenter + axisZ * radius);
		DrawSideLine(topCenter - axisZ * radius, bottomCenter - axisZ * radius);

		DrawArc(topCenter, axisX, axisY, radius, 0.0f, MathF.PI);
		DrawArc(topCenter, axisZ, axisY, radius, 0.0f, MathF.PI);
		DrawArc(bottomCenter, axisX, axisY, radius, MathF.PI, MathF.Tau);
		DrawArc(bottomCenter, axisZ, axisY, radius, MathF.PI, MathF.Tau);
	}

	private void DrawCircle(Vector3 center, Vector3 axisX, Vector3 axisZ, float radius)
	{
		var previous = center + axisX * radius;
		for (var segment = 1; segment <= RingSegments; segment++)
		{
			var angle = segment / (float)RingSegments * MathF.Tau;
			var next = center + (axisX * MathF.Cos(angle) + axisZ * MathF.Sin(angle)) * radius;
			_lineRenderer.DrawLine(previous, next, ColliderColor);
			previous = next;
		}
	}

	private void DrawArc(Vector3 center, Vector3 axisRadius, Vector3 axisHeight, float radius, float startAngle, float endAngle)
	{
		var previous = center + (axisRadius * MathF.Cos(startAngle) + axisHeight * MathF.Sin(startAngle)) * radius;
		for (var segment = 1; segment <= ArcSegments; segment++)
		{
			var t = segment / (float)ArcSegments;
			var angle = startAngle + (endAngle - startAngle) * t;
			var next = center + (axisRadius * MathF.Cos(angle) + axisHeight * MathF.Sin(angle)) * radius;
			_lineRenderer.DrawLine(previous, next, ColliderColor);
			previous = next;
		}
	}

	private void DrawSideLine(Vector3 startWorld, Vector3 endWorld)
	{
		_lineRenderer.DrawLine(startWorld, endWorld, ColliderColor);
	}

	private static Quaternion NormalizeOrIdentity(Quaternion rotation)
	{
		var lengthSquared = rotation.LengthSquared();
		if (lengthSquared <= 1e-8f || float.IsFinite(lengthSquared) == false)
		{
			return Quaternion.Identity;
		}

		return Quaternion.Normalize(rotation);
	}
}
