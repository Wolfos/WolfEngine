using System.Numerics;

namespace WolfEngine.Editor.Tooling.Splash;

/// <summary>
/// Bakes signed distance fields of filled regions, and how far along a traced outline each texel is, on a grid of
/// texels. All geometry is in texel units: texel (x, y) covers [x, x+1) x [y, y+1) and is sampled at its centre.
/// </summary>
/// <remarks>
/// A region is the union of its paths, so where shapes of one colour overlap, the edges hidden inside the union
/// are not edges of the field. Without that, an overlap would leave a half-covered seam through the shape.
/// Distances beyond <c>maxDistance</c> are clamped, and the nearest-edge search only looks that far.
/// </remarks>
internal static class DistanceFieldBaker
{
	private const int CellSize = 8;

	/// <summary>Signed distance from each texel centre to the region's edge, negative inside.</summary>
	public static float[] BakeSignedDistance(IReadOnlyList<VectorPath> region, int width, int height, float maxDistance)
	{
		ArgumentNullException.ThrowIfNull(region);
		ValidateGrid(width, height, maxDistance);

		var field = new float[width * height];
		if (region.Count == 0)
		{
			Array.Fill(field, maxDistance);
			return field;
		}

		var paths = region.Select(path => new PathEdges(path)).ToArray();
		var boundaries = FindVisibleBoundaries(paths);
		var grid = new SegmentGrid(boundaries, width, height, maxDistance);

		Parallel.For(0, height, y =>
		{
			var crossings = new List<(float X, int Path, int Direction)>();
			var windings = new int[paths.Length];
			var centerY = y + 0.5f;
			for (var pathIndex = 0; pathIndex < paths.Length; pathIndex++)
			{
				paths[pathIndex].CollectCrossings(centerY, pathIndex, crossings);
			}

			crossings.Sort((a, b) => a.X.CompareTo(b.X));
			var crossing = 0;
			var insideCount = 0;
			for (var x = 0; x < width; x++)
			{
				var centerX = x + 0.5f;
				while (crossing < crossings.Count &&
				       crossings[crossing].X <= centerX)
				{
					var (_, pathIndex, direction) = crossings[crossing];
					var wasInside = paths[pathIndex].IsInside(windings[pathIndex]);
					windings[pathIndex] += direction;
					var isInside = paths[pathIndex].IsInside(windings[pathIndex]);
					if (wasInside != isInside)
					{
						insideCount += isInside ? 1 : -1;
					}

					crossing++;
				}

				var distance = grid.FindNearest(new Vector2(centerX, centerY), out _, out _);
				field[y * width + x] = insideCount > 0 ? -distance : distance;
			}
		});

		return field;
	}

	/// <summary>
	/// For each texel, how far along <paramref name="contour"/> its nearest outline point is, traced from the
	/// contour's first point in both directions at once: 0 at the start, rising to 1 where the two halves meet,
	/// halfway round the outline. Texels further than <paramref name="maxDistance"/> from the outline read 1.
	/// </summary>
	/// <remarks>
	/// Storing the two-way parameter rather than the plain arc length keeps it continuous at the start, where the
	/// arc length jumps from the end of the contour back to zero. A filtered sample across that jump would light
	/// the start of the outline last.
	/// </remarks>
	public static float[] BakeMirroredTraceParameter(Vector2[] contour, int width, int height, float maxDistance)
	{
		ArgumentNullException.ThrowIfNull(contour);
		ValidateGrid(width, height, maxDistance);
		if (contour.Length < 3)
		{
			throw new ArgumentException("A traced outline needs at least three points.", nameof(contour));
		}

		var segments = new List<Segment>(contour.Length);
		var arcStarts = new float[contour.Length];
		var arcLengths = new float[contour.Length];
		var totalLength = 0.0f;
		for (var i = 0; i < contour.Length; i++)
		{
			var a = contour[i];
			var b = contour[(i + 1) % contour.Length];
			segments.Add(new Segment(a, b));
			arcStarts[i] = totalLength;
			arcLengths[i] = Vector2.Distance(a, b);
			totalLength += arcLengths[i];
		}

		if (totalLength <= 0.0f)
		{
			throw new ArgumentException("The traced outline has no length.", nameof(contour));
		}

		var grid = new SegmentGrid(segments, width, height, maxDistance);
		var field = new float[width * height];
		Parallel.For(0, height, y =>
		{
			for (var x = 0; x < width; x++)
			{
				var point = new Vector2(x + 0.5f, y + 0.5f);
				grid.FindNearest(point, out var segmentIndex, out var t);
				if (segmentIndex < 0)
				{
					field[y * width + x] = 1.0f;
					continue;
				}

				var along = (arcStarts[segmentIndex] + arcLengths[segmentIndex] * t) / totalLength;
				field[y * width + x] = 2.0f * MathF.Min(along, 1.0f - along);
			}
		});

		return field;
	}

	private static void ValidateGrid(int width, int height, float maxDistance)
	{
		if (width <= 0 || height <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(width), "The texel grid must not be empty.");
		}

		if (maxDistance <= 0.0f)
		{
			throw new ArgumentOutOfRangeException(nameof(maxDistance), "The distance range must be positive.");
		}
	}

	/// <summary>
	/// The pieces of the paths' edges with the union inside on exactly one side. Each edge is split where any other
	/// edge crosses it, so a piece is wholly visible or wholly hidden, then each piece is tested just off both sides
	/// of its midpoint.
	/// </summary>
	private static List<Segment> FindVisibleBoundaries(PathEdges[] paths)
	{
		var all = paths.SelectMany(path => path.Segments).ToArray();
		var result = new List<Segment>(all.Length);
		var splits = new List<float>();
		foreach (var segment in all)
		{
			var direction = segment.B - segment.A;
			var lengthSquared = direction.LengthSquared();
			if (lengthSquared <= 0.0f)
			{
				continue;
			}

			splits.Clear();
			splits.Add(0.0f);
			splits.Add(1.0f);
			foreach (var other in all)
			{
				var otherDirection = other.B - other.A;
				var denominator = Cross(direction, otherDirection);
				if (MathF.Abs(denominator) <= 1e-10f)
				{
					continue;
				}

				var t = Cross(other.A - segment.A, otherDirection) / denominator;
				var u = Cross(other.A - segment.A, direction) / denominator;
				if (t > 0.0f && t < 1.0f && u >= 0.0f && u <= 1.0f)
				{
					splits.Add(t);
				}
			}

			splits.Sort();
			var normal = Vector2.Normalize(new Vector2(-direction.Y, direction.X)) * 1e-3f;
			for (var i = 1; i < splits.Count; i++)
			{
				if (splits[i] - splits[i - 1] <= 1e-7f)
				{
					continue;
				}

				var middle = segment.A + direction * ((splits[i] + splits[i - 1]) * 0.5f);
				if (IsInsideUnion(paths, middle + normal) == IsInsideUnion(paths, middle - normal))
				{
					continue;
				}

				result.Add(new Segment(segment.A + direction * splits[i - 1], segment.A + direction * splits[i]));
			}
		}

		return result;
	}

	private static bool IsInsideUnion(PathEdges[] paths, Vector2 point)
	{
		foreach (var path in paths)
		{
			if (path.Contains(point))
			{
				return true;
			}
		}

		return false;
	}

	private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

	private readonly record struct Segment(Vector2 A, Vector2 B);

	/// <summary>One path's edges and its fill rule.</summary>
	private sealed class PathEdges
	{
		private readonly VectorFillRule _fillRule;

		public PathEdges(VectorPath path)
		{
			_fillRule = path.FillRule;
			var segments = new List<Segment>();
			foreach (var contour in path.Contours)
			{
				for (var i = 0; i < contour.Length; i++)
				{
					segments.Add(new Segment(contour[i], contour[(i + 1) % contour.Length]));
				}
			}

			Segments = segments;
		}

		public IReadOnlyList<Segment> Segments { get; }

		public bool IsInside(int winding) =>
			_fillRule == VectorFillRule.EvenOdd
				? (winding & 1) != 0
				: winding != 0;

		public bool Contains(Vector2 point)
		{
			var winding = 0;
			foreach (var (a, b) in Segments)
			{
				if (a.Y <= point.Y &&
				    b.Y > point.Y &&
				    Cross(b - a, point - a) > 0.0f)
				{
					winding++;
				}
				else if (b.Y <= point.Y &&
				         a.Y > point.Y &&
				         Cross(b - a, point - a) < 0.0f)
				{
					winding--;
				}
			}

			return IsInside(winding);
		}

		/// <summary>Where each edge crosses the horizontal line at <paramref name="y"/>, and in which direction.</summary>
		public void CollectCrossings(float y, int pathIndex, List<(float X, int Path, int Direction)> crossings)
		{
			foreach (var (a, b) in Segments)
			{
				var upward = a.Y <= y && b.Y > y;
				var downward = b.Y <= y && a.Y > y;
				if (upward == false && downward == false)
				{
					continue;
				}

				var x = a.X + (y - a.Y) / (b.Y - a.Y) * (b.X - a.X);
				crossings.Add((x, pathIndex, upward ? 1 : -1));
			}
		}
	}

	/// <summary>
	/// Buckets segments into square cells, each listing the segments within <c>maxDistance</c> of it, so a texel's
	/// nearest edge is found among a handful of candidates instead of the whole outline.
	/// </summary>
	private sealed class SegmentGrid
	{
		private readonly IReadOnlyList<Segment> _segments;
		private readonly List<int>?[] _cells;
		private readonly int _columns;
		private readonly int _rows;
		private readonly float _maxDistance;

		public SegmentGrid(IReadOnlyList<Segment> segments, int width, int height, float maxDistance)
		{
			_segments = segments;
			_maxDistance = maxDistance;
			_columns = (width + CellSize - 1) / CellSize;
			_rows = (height + CellSize - 1) / CellSize;
			_cells = new List<int>?[_columns * _rows];
			for (var i = 0; i < segments.Count; i++)
			{
				var (a, b) = segments[i];
				var minColumn = CellIndex(MathF.Min(a.X, b.X) - maxDistance, _columns);
				var maxColumn = CellIndex(MathF.Max(a.X, b.X) + maxDistance, _columns);
				var minRow = CellIndex(MathF.Min(a.Y, b.Y) - maxDistance, _rows);
				var maxRow = CellIndex(MathF.Max(a.Y, b.Y) + maxDistance, _rows);
				for (var row = minRow; row <= maxRow; row++)
				{
					for (var column = minColumn; column <= maxColumn; column++)
					{
						var cell = row * _columns + column;
						_cells[cell] ??= new List<int>();
						_cells[cell]!.Add(i);
					}
				}
			}
		}

		/// <summary>The distance to the nearest segment, clamped to the grid's range.</summary>
		/// <param name="segmentIndex">The nearest segment, or -1 when none is in range.</param>
		/// <param name="t">Where on that segment the nearest point is, from 0 at its start to 1 at its end.</param>
		public float FindNearest(Vector2 point, out int segmentIndex, out float t)
		{
			segmentIndex = -1;
			t = 0.0f;
			var column = Math.Clamp((int)(point.X / CellSize), 0, _columns - 1);
			var row = Math.Clamp((int)(point.Y / CellSize), 0, _rows - 1);
			var candidates = _cells[row * _columns + column];
			if (candidates is null)
			{
				return _maxDistance;
			}

			var best = _maxDistance;
			foreach (var index in candidates)
			{
				var (a, b) = _segments[index];
				var direction = b - a;
				var lengthSquared = direction.LengthSquared();
				var along = lengthSquared <= 0.0f
					? 0.0f
					: Math.Clamp(Vector2.Dot(point - a, direction) / lengthSquared, 0.0f, 1.0f);
				var distance = Vector2.Distance(point, a + direction * along);
				if (distance < best)
				{
					best = distance;
					segmentIndex = index;
					t = along;
				}
			}

			return best;
		}

		private static int CellIndex(float coordinate, int count) =>
			Math.Clamp((int)MathF.Floor(coordinate / CellSize), 0, count - 1);
	}
}
