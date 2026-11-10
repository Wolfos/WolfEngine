using System.Numerics;

namespace WolfEngine.Modeling;

/// <summary>
/// Extracts the zero surface of a distance grid as triangles, with one vertex per cell the surface passes through.
/// This is the dense reference surface: what the compiled mesh is measured against, not what ships.
/// <para>
/// Each vertex sits at the mean of the cell's edge crossings, so sharp edges come out slightly rounded at grid
/// resolution. That is acceptable for a reference; the remesher projects onto the true field instead.
/// </para>
/// <para>
/// Both passes run one Z slice per worker. Each slice builds its own lists, and the slices are then joined in order,
/// so the output is identical however the work was scheduled.
/// </para>
/// </summary>
public static class SurfaceNets
{
	// Corner offsets of a cell, indexed by the bit pattern x | y << 1 | z << 2.
	private static readonly int[] CornerX = [0, 1, 0, 1, 0, 1, 0, 1];
	private static readonly int[] CornerY = [0, 0, 1, 1, 0, 0, 1, 1];
	private static readonly int[] CornerZ = [0, 0, 0, 0, 1, 1, 1, 1];

	// The twelve cell edges as corner pairs.
	private static readonly int[] EdgeStart = [0, 2, 4, 6, 0, 1, 4, 5, 0, 1, 2, 3];
	private static readonly int[] EdgeEnd = [1, 3, 5, 7, 2, 3, 6, 7, 4, 5, 6, 7];

	public static ShapeMesh Extract(SdfGrid grid)
	{
		ArgumentNullException.ThrowIfNull(grid);

		var layout = grid.Layout;
		var cellsX = layout.SizeX - 1;
		var cellsY = layout.SizeY - 1;
		var cellsZ = layout.SizeZ - 1;
		if (cellsX < 1 || cellsY < 1 || cellsZ < 1)
		{
			return ShapeMesh.Empty;
		}

		var cellVertex = new int[cellsX * cellsY * cellsZ];
		var vertexSlices = new VertexSlice[cellsZ];
		Parallel.For(0, cellsZ, z =>
		{
			vertexSlices[z] = PlaceVertices(grid, z, cellsX, cellsY, cellVertex);
		});

		// Slice-local vertex numbers become global ones once every slice's count is known.
		var vertexOffsets = new int[cellsZ];
		var vertexCount = 0;
		for (var z = 0; z < cellsZ; z++)
		{
			vertexOffsets[z] = vertexCount;
			vertexCount += vertexSlices[z].Positions.Count;
		}

		var positions = new Vector3[vertexCount];
		var normals = new Vector3[vertexCount];
		Parallel.For(0, cellsZ, z =>
		{
			var slice = vertexSlices[z];
			var offset = vertexOffsets[z];
			slice.Positions.CopyTo(positions, offset);
			slice.Normals.CopyTo(normals, offset);

			var start = z * cellsX * cellsY;
			var end = start + cellsX * cellsY;
			for (var i = start; i < end; i++)
			{
				if (cellVertex[i] >= 0)
				{
					cellVertex[i] += offset;
				}
			}
		});

		var indexSlices = new List<uint>[layout.SizeZ];
		Parallel.For(0, layout.SizeZ, z =>
		{
			indexSlices[z] = ConnectQuads(grid, z, cellsX, cellsY, cellsZ, cellVertex, positions);
		});

		var indexCount = 0;
		foreach (var slice in indexSlices)
		{
			indexCount += slice.Count;
		}

		var indices = new uint[indexCount];
		var indexOffset = 0;
		foreach (var slice in indexSlices)
		{
			slice.CopyTo(indices, indexOffset);
			indexOffset += slice.Count;
		}

		return new ShapeMesh(positions, normals, indices);
	}

	private static VertexSlice PlaceVertices(SdfGrid grid, int z, int cellsX, int cellsY, int[] cellVertex)
	{
		var layout = grid.Layout;
		var distances = grid.Distances;
		var strideY = layout.SizeX;
		var strideZ = layout.SizeX * layout.SizeY;

		// Offsets of the eight corners from a cell's first sample, in the flat distance array.
		Span<int> cornerOffsets = stackalloc int[8];
		for (var c = 0; c < 8; c++)
		{
			cornerOffsets[c] = CornerX[c] + CornerY[c] * strideY + CornerZ[c] * strideZ;
		}

		var slice = new VertexSlice();
		Span<float> corners = stackalloc float[8];
		for (var y = 0; y < cellsY; y++)
		{
			var sampleRow = z * strideZ + y * strideY;
			var cellRow = (z * cellsY + y) * cellsX;
			for (var x = 0; x < cellsX; x++)
			{
				var sample = sampleRow + x;
				var insideMask = 0;
				for (var c = 0; c < 8; c++)
				{
					var value = distances[sample + cornerOffsets[c]];
					corners[c] = value;
					if (value < 0.0f)
					{
						insideMask |= 1 << c;
					}
				}

				if (insideMask == 0 || insideMask == 0xFF)
				{
					cellVertex[cellRow + x] = -1;
					continue;
				}

				var sum = Vector3.Zero;
				var crossings = 0;
				for (var e = 0; e < 12; e++)
				{
					var a = EdgeStart[e];
					var b = EdgeEnd[e];
					var aInside = (insideMask & (1 << a)) != 0;
					var bInside = (insideMask & (1 << b)) != 0;
					if (aInside == bInside)
					{
						continue;
					}

					var t = corners[a] / (corners[a] - corners[b]);
					var start = new Vector3(CornerX[a], CornerY[a], CornerZ[a]);
					var end = new Vector3(CornerX[b], CornerY[b], CornerZ[b]);
					sum += Vector3.Lerp(start, end, t);
					crossings++;
				}

				var local = sum / crossings;
				cellVertex[cellRow + x] = slice.Positions.Count;
				slice.Positions.Add(layout.Origin + (new Vector3(x, y, z) + local) * layout.CellSize);
				slice.Normals.Add(GetTrilinearGradient(corners, local));
			}
		}

		return slice;
	}

	/// <summary>Emits the quads for every sign-changing grid edge that starts in sample slice <paramref name="z"/>.</summary>
	private static List<uint> ConnectQuads(
		SdfGrid grid,
		int z,
		int cellsX,
		int cellsY,
		int cellsZ,
		int[] cellVertex,
		Vector3[] positions)
	{
		var layout = grid.Layout;
		var distances = grid.Distances;
		var strideY = layout.SizeX;
		var strideZ = layout.SizeX * layout.SizeY;
		var indices = new List<uint>();

		for (var y = 0; y < layout.SizeY; y++)
		{
			for (var x = 0; x < layout.SizeX; x++)
			{
				var sample = z * strideZ + y * strideY + x;
				var inside = distances[sample] < 0.0f;

				// Edge along +X: the four cells around it differ in Y and Z.
				if (x < cellsX &&
				    y > 0 &&
				    z > 0 &&
				    y < cellsY &&
				    z < cellsZ &&
				    inside != distances[sample + 1] < 0.0f)
				{
					EmitQuad(
						Cell(x, y - 1, z - 1),
						Cell(x, y, z - 1),
						Cell(x, y, z),
						Cell(x, y - 1, z),
						inside);
				}

				// Edge along +Y: the cells differ in Z and X.
				if (y < cellsY &&
				    x > 0 &&
				    z > 0 &&
				    x < cellsX &&
				    z < cellsZ &&
				    inside != distances[sample + strideY] < 0.0f)
				{
					EmitQuad(
						Cell(x - 1, y, z - 1),
						Cell(x - 1, y, z),
						Cell(x, y, z),
						Cell(x, y, z - 1),
						inside);
				}

				// Edge along +Z: the cells differ in X and Y.
				if (z < cellsZ &&
				    x > 0 &&
				    y > 0 &&
				    x < cellsX &&
				    y < cellsY &&
				    inside != distances[sample + strideZ] < 0.0f)
				{
					EmitQuad(
						Cell(x - 1, y - 1, z),
						Cell(x, y - 1, z),
						Cell(x, y, z),
						Cell(x - 1, y, z),
						inside);
				}
			}
		}

		return indices;

		int Cell(int cx, int cy, int cz)
		{
			return cellVertex[(cz * cellsY + cy) * cellsX + cx];
		}

		// The four vertices circle the edge so that cross(v1 - v0, v2 - v0) points along it. That is outward when the
		// edge leaves the solid, and inward when it enters it, in which case the quad is flipped.
		void EmitQuad(int v0, int v1, int v2, int v3, bool startInside)
		{
			if (v0 < 0 || v1 < 0 || v2 < 0 || v3 < 0)
			{
				return;
			}

			if (startInside == false)
			{
				(v1, v3) = (v3, v1);
			}

			// Split along the shorter diagonal, which keeps the two triangles closer to the surface.
			var diagonal02 = Vector3.DistanceSquared(positions[v0], positions[v2]);
			var diagonal13 = Vector3.DistanceSquared(positions[v1], positions[v3]);
			if (diagonal02 <= diagonal13)
			{
				AddTriangle(v0, v1, v2);
				AddTriangle(v0, v2, v3);
			}
			else
			{
				AddTriangle(v0, v1, v3);
				AddTriangle(v1, v2, v3);
			}
		}

		void AddTriangle(int a, int b, int c)
		{
			indices.Add((uint)a);
			indices.Add((uint)b);
			indices.Add((uint)c);
		}
	}

	/// <summary>Gradient of the trilinear interpolant of the eight corner distances, at a point inside the cell.</summary>
	private static Vector3 GetTrilinearGradient(ReadOnlySpan<float> c, Vector3 t)
	{
		var dx =
			(1 - t.Y) * (1 - t.Z) * (c[1] - c[0]) +
			t.Y * (1 - t.Z) * (c[3] - c[2]) +
			(1 - t.Y) * t.Z * (c[5] - c[4]) +
			t.Y * t.Z * (c[7] - c[6]);
		var dy =
			(1 - t.X) * (1 - t.Z) * (c[2] - c[0]) +
			t.X * (1 - t.Z) * (c[3] - c[1]) +
			(1 - t.X) * t.Z * (c[6] - c[4]) +
			t.X * t.Z * (c[7] - c[5]);
		var dz =
			(1 - t.X) * (1 - t.Y) * (c[4] - c[0]) +
			t.X * (1 - t.Y) * (c[5] - c[1]) +
			(1 - t.X) * t.Y * (c[6] - c[2]) +
			t.X * t.Y * (c[7] - c[3]);
		var gradient = new Vector3(dx, dy, dz);
		var length = gradient.Length();

		return length > 1.0e-12f ? gradient / length : Vector3.UnitY;
	}

	private sealed class VertexSlice
	{
		public List<Vector3> Positions { get; } = new();
		public List<Vector3> Normals { get; } = new();
	}
}
