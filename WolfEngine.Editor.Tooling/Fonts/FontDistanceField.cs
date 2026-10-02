using System.Numerics;
using Remora.MSDFGen;
using Remora.MSDFGen.Graphics;

namespace WolfEngine.Editor.Tooling.Fonts;

/// <summary>Overlap-safe scalar distance with OpenType's nonzero fill rule.</summary>
internal static class FontDistanceField
{
	public static void Generate(Pixmap<float> field, Shape shape, float scale, float minX, float minY, float range)
	{
		// Remora's legacy contour-sign combination can produce holes at edge junctions
		// and overlapping font contours. Determine fill from scanline winding instead.
		// Flatten within 1/16 texel and discard internal boundaries of overlapping
		// contours. Otherwise a stem/crossbar overlap leaves a visible 0.5-distance seam.
		var lines = new List<(Vector2 A, Vector2 B)>();
		var edges = shape.Contours.SelectMany(c => c.Edges).ToArray();
		foreach (var edge in edges) Flatten(edge, 0, 1, edge.GetPoint(0), edge.GetPoint(1), 0);
		var boundaries = GetBoundaries(lines, 0.001f / scale);
		var intersections = new List<(float X, int Winding)>();
		for (var y = 0; y < field.Height; y++)
		{
			var py = (y + minY + 0.5f) / scale;
			intersections.Clear();
			foreach (var (a, b) in lines)
			{
				if ((a.Y <= py && b.Y > py) || (b.Y <= py && a.Y > py))
					intersections.Add((a.X + (py - a.Y) / (b.Y - a.Y) * (b.X - a.X), b.Y > a.Y ? 1 : -1));
			}
			intersections.Sort((a, b) => a.X.CompareTo(b.X));
			var crossing = 0;
			var winding = 0;
			for (var x = 0; x < field.Width; x++)
			{
				var point = new Vector2((x + minX + 0.5f) / scale, py);
				while (crossing < intersections.Count && intersections[crossing].X <= point.X)
					winding += intersections[crossing++].Winding;
				var distance = double.PositiveInfinity;
				foreach (var (a, b) in boundaries) distance = Math.Min(distance, Deviation(point, a, b));
				field[x, y] = (float)(distance * scale / range * (winding == 0 ? -1 : 1)) + 0.5f;
			}
		}

		void Flatten(EdgeSegment edge, double t0, double t1, Vector2 a, Vector2 b, int depth)
		{
			var mid = (t0 + t1) * 0.5;
			var m = edge.GetPoint(mid);
			var q1 = edge.GetPoint((t0 + mid) * 0.5); var q3 = edge.GetPoint((mid + t1) * 0.5);
			var tolerance = 0.0625f / scale;
			if (depth < 16 && (Deviation(m, a, b) > tolerance || Deviation(q1, a, b) > tolerance || Deviation(q3, a, b) > tolerance))
			{
				Flatten(edge, t0, mid, a, m, depth + 1);
				Flatten(edge, mid, t1, m, b, depth + 1);
			}
			else lines.Add((a, b));
		}
	}

	private static List<(Vector2 A, Vector2 B)> GetBoundaries(List<(Vector2 A, Vector2 B)> lines, float epsilon)
	{
		var result = new List<(Vector2, Vector2)>();
		var splits = new List<float>();
		foreach (var (a, b) in lines)
		{
			var direction = b - a;
			var lengthSquared = direction.LengthSquared();
			if (lengthSquared == 0) continue;
			splits.Clear(); splits.Add(0); splits.Add(1);
			foreach (var (c, d) in lines)
			{
				var other = d - c;
				var denominator = Cross(direction, other);
				if (Math.Abs(denominator) > 1e-10f)
				{
					var t = Cross(c - a, other) / denominator;
					var u = Cross(c - a, direction) / denominator;
					if (t > 0 && t < 1 && u >= 0 && u <= 1) splits.Add(t);
				}
				else if (Math.Abs(Cross(c - a, direction)) < 1e-10f)
				{
					Add(Vector2.Dot(c - a, direction) / lengthSquared);
					Add(Vector2.Dot(d - a, direction) / lengthSquared);
				}
			}
			splits.Sort();
			var normal = new Vector2(-direction.Y, direction.X) / MathF.Sqrt(lengthSquared) * epsilon;
			for (var i = 1; i < splits.Count; i++)
			{
				if (splits[i] - splits[i - 1] <= 1e-7f) continue;
				var mid = a + direction * ((splits[i] + splits[i - 1]) * 0.5f);
				if (Inside(mid + normal) == Inside(mid - normal)) continue;
				result.Add((a + direction * splits[i - 1], a + direction * splits[i]));
			}
		}
		return result;

		void Add(float t) { if (t > 0 && t < 1) splits.Add(t); }
		bool Inside(Vector2 p)
		{
			var winding = 0;
			foreach (var (a, b) in lines)
			{
				if (a.Y <= p.Y && b.Y > p.Y && Cross(b - a, p - a) > 0) winding++;
				else if (b.Y <= p.Y && a.Y > p.Y && Cross(b - a, p - a) < 0) winding--;
			}
			return winding != 0;
		}
	}

	private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

	private static float Deviation(Vector2 p, Vector2 a, Vector2 b)
	{
		var direction = b - a;
		var lengthSquared = direction.LengthSquared();
		return Vector2.Distance(p, lengthSquared == 0 ? a : a + direction * Math.Clamp(Vector2.Dot(p - a, direction) / lengthSquared, 0, 1));
	}
}
