using System;
using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Editor.UI;
using WolfEngine.Physics;
using WolfEngine.Rendering;

namespace WolfEngine.Editor;

public sealed class SphereColliderGizmoDrawer : IOnDrawGizmos
{
	private const int RingSegments = 24;
	private static readonly ColorRGBA ColliderColor = new(0.2f, 0.8f, 1.0f, 1.0f);
	private readonly IGizmoLineRenderer _lineRenderer;

	public SphereColliderGizmoDrawer(IGizmoLineRenderer lineRenderer)
	{
		_lineRenderer = lineRenderer ?? throw new ArgumentNullException(nameof(lineRenderer));
	}

	public void OnDrawGizmos(World world)
	{
		if (EditorGui.HasSelectedEntity == false)
		{
			return;
		}

		foreach (var entry in world.View<SphereCollider>())
		{
			if (entry.Entity == EditorGui.SelectedEntity && world.HasComponent<WorldTransform>(entry.Entity))
			{
				DrawSphere(world.GetComponent<WorldTransform>(entry.Entity).LocalToWorld, entry.First);
			}
		}
	}

	public WorldTag GetTag() => WorldTag.Authoring;

	private void DrawSphere(Matrix4x4 localToWorld, in SphereCollider collider)
	{
		var scale = Vector3.One;
		var rotation = Quaternion.Identity;
		if (Matrix4x4.Decompose(localToWorld, out var worldScale, out var worldRotation, out _))
		{
			scale = new Vector3(MathF.Abs(worldScale.X), MathF.Abs(worldScale.Y), MathF.Abs(worldScale.Z));
			rotation = NormalizeOrIdentity(worldRotation);
		}

		var center = Vector3.Transform(collider.Center, localToWorld);
		var radius = MathF.Max(0.001f, MathF.Abs(collider.Radius) * MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z)));
		var axisX = Vector3.Transform(Vector3.UnitX, rotation);
		var axisY = Vector3.Transform(Vector3.UnitY, rotation);
		var axisZ = Vector3.Transform(Vector3.UnitZ, rotation);
		DrawCircle(center, axisX, axisY, radius);
		DrawCircle(center, axisX, axisZ, radius);
		DrawCircle(center, axisY, axisZ, radius);
	}

	private void DrawCircle(Vector3 center, Vector3 axisA, Vector3 axisB, float radius)
	{
		var previous = center + axisA * radius;
		for (var segment = 1; segment <= RingSegments; segment++)
		{
			var angle = segment / (float)RingSegments * MathF.Tau;
			var next = center + (axisA * MathF.Cos(angle) + axisB * MathF.Sin(angle)) * radius;
			_lineRenderer.DrawLine(previous, next, ColliderColor);
			previous = next;
		}
	}

	private static Quaternion NormalizeOrIdentity(Quaternion rotation)
	{
		var lengthSquared = rotation.LengthSquared();
		return lengthSquared <= 1e-8f || float.IsFinite(lengthSquared) == false ? Quaternion.Identity : Quaternion.Normalize(rotation);
	}
}
