using System.Numerics;

namespace WolfEngine.Modeling;

/// <summary>
/// What each primitive's <see cref="ShapeNode.Dimensions"/> mean, their defaults, and the CPU reference distance
/// functions. The GPU sampler in <c>shape_sdf.compute.slang</c> must evaluate exactly the same functions; keep the
/// two in step.
/// </summary>
public static class ShapePrimitives
{
	public const float MinDimension = 0.0001f;

	public static readonly ShapePrimitiveKind[] All =
	[
		ShapePrimitiveKind.Sphere,
		ShapePrimitiveKind.Box,
		ShapePrimitiveKind.RoundedBox,
		ShapePrimitiveKind.Capsule,
		ShapePrimitiveKind.Cylinder,
		ShapePrimitiveKind.Torus,
		ShapePrimitiveKind.Cone
	];

	public static string GetDisplayName(ShapePrimitiveKind kind)
	{
		return kind switch
		{
			ShapePrimitiveKind.Sphere => "Sphere",
			ShapePrimitiveKind.Box => "Box",
			ShapePrimitiveKind.RoundedBox => "Rounded Box",
			ShapePrimitiveKind.Capsule => "Capsule",
			ShapePrimitiveKind.Cylinder => "Cylinder",
			ShapePrimitiveKind.Torus => "Torus",
			ShapePrimitiveKind.Cone => "Cone",
			_ => kind.ToString()
		};
	}

	/// <summary>Labels of the dimension components a primitive uses, in component order. Unused components are omitted.</summary>
	public static string[] GetDimensionLabels(ShapePrimitiveKind kind)
	{
		return kind switch
		{
			ShapePrimitiveKind.Sphere => ["Radius"],
			ShapePrimitiveKind.Box => ["Half Width", "Half Height", "Half Depth"],
			ShapePrimitiveKind.RoundedBox => ["Half Width", "Half Height", "Half Depth", "Rounding"],
			ShapePrimitiveKind.Capsule => ["Radius", "Half Height"],
			ShapePrimitiveKind.Cylinder => ["Radius", "Half Height"],
			ShapePrimitiveKind.Torus => ["Major Radius", "Minor Radius"],
			ShapePrimitiveKind.Cone => ["Base Radius", "Half Height"],
			_ => []
		};
	}

	public static Vector4 GetDefaultDimensions(ShapePrimitiveKind kind)
	{
		return kind switch
		{
			ShapePrimitiveKind.Sphere => new Vector4(0.5f, 0.0f, 0.0f, 0.0f),
			ShapePrimitiveKind.Box => new Vector4(0.5f, 0.5f, 0.5f, 0.0f),
			ShapePrimitiveKind.RoundedBox => new Vector4(0.5f, 0.5f, 0.5f, 0.1f),
			ShapePrimitiveKind.Capsule => new Vector4(0.25f, 0.5f, 0.0f, 0.0f),
			ShapePrimitiveKind.Cylinder => new Vector4(0.5f, 0.5f, 0.0f, 0.0f),
			ShapePrimitiveKind.Torus => new Vector4(0.5f, 0.15f, 0.0f, 0.0f),
			ShapePrimitiveKind.Cone => new Vector4(0.5f, 0.5f, 0.0f, 0.0f),
			_ => Vector4.Zero
		};
	}

	/// <summary>
	/// Clamps dimensions to what a primitive can represent, such as a rounding no larger than the smallest half
	/// extent. Both samplers see sanitised dimensions, so they never disagree on degenerate input.
	/// </summary>
	public static Vector4 Sanitize(ShapePrimitiveKind kind, Vector4 dimensions)
	{
		var d = Vector4.Max(dimensions, new Vector4(MinDimension));
		switch (kind)
		{
			case ShapePrimitiveKind.RoundedBox:
			{
				var maxRounding = MathF.Min(d.X, MathF.Min(d.Y, d.Z));
				d.W = Math.Clamp(dimensions.W, 0.0f, maxRounding);
				return d;
			}

			case ShapePrimitiveKind.Box:
				return d with { W = 0.0f };

			default:
				return d;
		}
	}

	/// <summary>Half extents of the primitive's local bounding box.</summary>
	public static Vector3 GetLocalHalfExtents(ShapePrimitiveKind kind, Vector4 dimensions)
	{
		var d = Sanitize(kind, dimensions);
		return kind switch
		{
			ShapePrimitiveKind.Sphere => new Vector3(d.X),
			ShapePrimitiveKind.Box => new Vector3(d.X, d.Y, d.Z),
			ShapePrimitiveKind.RoundedBox => new Vector3(d.X, d.Y, d.Z),
			ShapePrimitiveKind.Capsule => new Vector3(d.X, d.Y + d.X, d.X),
			ShapePrimitiveKind.Cylinder => new Vector3(d.X, d.Y, d.X),
			ShapePrimitiveKind.Torus => new Vector3(d.X + d.Y, d.Y, d.X + d.Y),
			ShapePrimitiveKind.Cone => new Vector3(d.X, d.Y, d.X),
			_ => Vector3.Zero
		};
	}

	/// <summary>Signed distance from a point in the primitive's local space. Expects sanitised dimensions.</summary>
	public static float Evaluate(ShapePrimitiveKind kind, Vector4 d, Vector3 p)
	{
		switch (kind)
		{
			case ShapePrimitiveKind.Sphere:
				return p.Length() - d.X;

			case ShapePrimitiveKind.Box:
				return Box(p, new Vector3(d.X, d.Y, d.Z));

			case ShapePrimitiveKind.RoundedBox:
				return Box(p, new Vector3(d.X, d.Y, d.Z) - new Vector3(d.W)) - d.W;

			case ShapePrimitiveKind.Capsule:
			{
				var q = p with { Y = p.Y - Math.Clamp(p.Y, -d.Y, d.Y) };
				return q.Length() - d.X;
			}

			case ShapePrimitiveKind.Cylinder:
			{
				var q = new Vector2(new Vector2(p.X, p.Z).Length(), MathF.Abs(p.Y)) - new Vector2(d.X, d.Y);
				return MathF.Min(MathF.Max(q.X, q.Y), 0.0f) + Vector2.Max(q, Vector2.Zero).Length();
			}

			case ShapePrimitiveKind.Torus:
			{
				var q = new Vector2(new Vector2(p.X, p.Z).Length() - d.X, p.Y);
				return q.Length() - d.Y;
			}

			case ShapePrimitiveKind.Cone:
				return CappedCone(p, d.Y, d.X, 0.0f);

			default:
				return float.MaxValue;
		}
	}

	private static float Box(Vector3 p, Vector3 halfExtents)
	{
		var q = Vector3.Abs(p) - halfExtents;
		return Vector3.Max(q, Vector3.Zero).Length() + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0.0f);
	}

	/// <summary>Exact distance to a cone frustum along Y, bottom radius r1 at -h and top radius r2 at +h.</summary>
	private static float CappedCone(Vector3 p, float h, float r1, float r2)
	{
		var q = new Vector2(new Vector2(p.X, p.Z).Length(), p.Y);
		var k1 = new Vector2(r2, h);
		var k2 = new Vector2(r2 - r1, 2.0f * h);
		var ca = new Vector2(q.X - MathF.Min(q.X, q.Y < 0.0f ? r1 : r2), MathF.Abs(q.Y) - h);
		var cb = q - k1 + k2 * Math.Clamp(Vector2.Dot(k1 - q, k2) / Vector2.Dot(k2, k2), 0.0f, 1.0f);
		var sign = cb.X < 0.0f && ca.Y < 0.0f ? -1.0f : 1.0f;
		return sign * MathF.Sqrt(MathF.Min(Vector2.Dot(ca, ca), Vector2.Dot(cb, cb)));
	}
}
