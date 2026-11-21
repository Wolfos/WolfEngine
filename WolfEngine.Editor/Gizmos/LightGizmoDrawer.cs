using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Editor.UI;
using WolfEngine.Rendering;

namespace WolfEngine.Editor;

/// <summary>
/// Shows what a selected light does: the direction a directional light shines in, and the range of a
/// point light.
/// </summary>
public sealed class LightGizmoDrawer : IOnDrawGizmos
{
	/// <summary>Length of a directional light's arrow. Directional lights have no size, so this is arbitrary.</summary>
	public const float DirectionArrowLength = 2.0f;

	private const float DirectionDiscRadius = 0.5f;
	private const float ArrowHeadLength = 0.4f;
	private const float ArrowHeadRadius = 0.15f;
	private static readonly ColorRGBA LightColor = new(1.0f, 0.92f, 0.4f, 1.0f);

	private readonly IGizmoLineRenderer _lineRenderer;

	public LightGizmoDrawer(IGizmoLineRenderer lineRenderer)
	{
		_lineRenderer = lineRenderer ?? throw new ArgumentNullException(nameof(lineRenderer));
	}

	public void OnDrawGizmos(World world)
	{
		if (EditorGui.SelectedEntities.Count == 0)
		{
			return;
		}

		foreach (var entry in world.View<Light>())
		{
			if (EditorGui.SelectedEntities.Contains(entry.Entity) == false ||
			    world.HasComponent<WorldTransform>(entry.Entity) == false)
			{
				continue;
			}

			var localToWorld = GizmoShapes.WithoutScale(world.GetComponent<WorldTransform>(entry.Entity).LocalToWorld);
			switch (entry.First.Type)
			{
				case LightType.Directional:
					DrawDirection(localToWorld);
					break;

				case LightType.Point:
					DrawRange(localToWorld, entry.First.Range);
					break;
			}
		}
	}

	public WorldTag GetTag() => WorldTag.Authoring;

	private void DrawDirection(Matrix4x4 localToWorld)
	{
		var origin = localToWorld.Translation;
		var forward = Vector3.TransformNormal(Vector3.UnitZ, localToWorld);
		var right = Vector3.TransformNormal(Vector3.UnitX, localToWorld);
		var up = Vector3.TransformNormal(Vector3.UnitY, localToWorld);
		var tip = origin + forward * DirectionArrowLength;

		// A disc of parallel rays reads as "light shining this way" better than a single line does.
		GizmoShapes.DrawCircle(_lineRenderer, origin, right, up, DirectionDiscRadius, LightColor);
		_lineRenderer.DrawLine(origin, tip, LightColor);

		for (var ray = 0; ray < 4; ray++)
		{
			var angle = ray / 4.0f * MathF.Tau;
			var offset = (right * MathF.Cos(angle) + up * MathF.Sin(angle)) * DirectionDiscRadius;
			_lineRenderer.DrawLine(origin + offset, tip + offset, LightColor);
		}

		var headBase = tip - forward * ArrowHeadLength;
		for (var spoke = 0; spoke < 4; spoke++)
		{
			var angle = spoke / 4.0f * MathF.Tau;
			var offset = (right * MathF.Cos(angle) + up * MathF.Sin(angle)) * ArrowHeadRadius;
			_lineRenderer.DrawLine(tip, headBase + offset, LightColor);
		}
	}

	private void DrawRange(Matrix4x4 localToWorld, float range)
	{
		if (float.IsFinite(range) == false || range <= 0.0f)
		{
			return;
		}

		var center = localToWorld.Translation;
		var axisX = Vector3.TransformNormal(Vector3.UnitX, localToWorld);
		var axisY = Vector3.TransformNormal(Vector3.UnitY, localToWorld);
		var axisZ = Vector3.TransformNormal(Vector3.UnitZ, localToWorld);
		GizmoShapes.DrawCircle(_lineRenderer, center, axisX, axisY, range, LightColor);
		GizmoShapes.DrawCircle(_lineRenderer, center, axisX, axisZ, range, LightColor);
		GizmoShapes.DrawCircle(_lineRenderer, center, axisY, axisZ, range, LightColor);
	}
}
