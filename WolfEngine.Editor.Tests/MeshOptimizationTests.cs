using System.Numerics;
using WolfEngine.Importing;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class MeshOptimizationTests
{
	private static readonly Vector4 P0 = new(0.0f, 0.0f, 0.0f, 1.0f);
	private static readonly Vector4 P1 = new(1.0f, 0.0f, 0.0f, 1.0f);
	private static readonly Vector4 P2 = new(0.0f, 1.0f, 0.0f, 1.0f);
	private static readonly Vector4 P3 = new(1.0f, 1.0f, 0.0f, 1.0f);

	[Test]
	public void Optimize_WeldsVerticesThatShareAllAttributes()
	{
		// A quad exported as two unindexed triangles: the shared edge is duplicated.
		var geometry = new MeshGeometry(
			[P0, P1, P2, P2, P1, P3],
			Enumerable.Repeat(Vector3.UnitZ, 6).ToArray(),
			Enumerable.Repeat(Vector2.Zero, 6).ToArray(),
			Enumerable.Repeat(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), 6).ToArray(),
			null,
			null,
			[0, 1, 2, 3, 4, 5]);

		var optimized = MeshOptimization.Optimize(geometry);

		Assert.That(optimized.Positions, Has.Length.EqualTo(4));
		Assert.That(optimized.Indices, Has.Length.EqualTo(6));
		Assert.That(DescribeTriangles(optimized), Is.EquivalentTo(DescribeTriangles(geometry)));
	}

	[Test]
	public void Optimize_KeepsVerticesThatDifferInAnyAttribute()
	{
		// The same quad, but the two triangles carry opposing normals — a hard edge that must survive.
		var geometry = new MeshGeometry(
			[P0, P1, P2, P2, P1, P3],
			[Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ],
			Enumerable.Repeat(Vector2.Zero, 6).ToArray(),
			Enumerable.Repeat(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), 6).ToArray(),
			null,
			null,
			[0, 1, 2, 3, 4, 5]);

		var optimized = MeshOptimization.Optimize(geometry);

		Assert.That(optimized.Positions, Has.Length.EqualTo(6));
		Assert.That(DescribeTriangles(optimized), Is.EquivalentTo(DescribeTriangles(geometry)));
	}

	[Test]
	public void Optimize_OrdersVerticesInTheOrderTheIndexBufferFirstReadsThem()
	{
		var geometry = new MeshGeometry(
			[P0, P1, P2, P3],
			null,
			null,
			null,
			null,
			null,
			[2, 3, 1, 0, 1, 2]);

		var optimized = MeshOptimization.Optimize(geometry);

		// Vertex fetch optimization means the first triangle reads the lowest vertices, and no index
		// jumps past a vertex that has not been read yet.
		var highestSeen = -1;
		foreach (var index in optimized.Indices)
		{
			Assert.That((int)index, Is.LessThanOrEqualTo(highestSeen + 1));
			highestSeen = System.Math.Max(highestSeen, (int)index);
		}

		Assert.That(DescribeTriangles(optimized), Is.EquivalentTo(DescribeTriangles(geometry)));
	}

	[Test]
	public void Optimize_RemovesDegenerateTrianglesAndTheVerticesTheyStrandBehind()
	{
		// P3 is only referenced by a triangle that covers no area, so it should not survive either.
		var geometry = new MeshGeometry(
			[P0, P1, P2, P3],
			null,
			null,
			null,
			null,
			null,
			[0, 1, 2, 3, 3, 1]);

		var optimized = MeshOptimization.Optimize(geometry);

		Assert.That(optimized.Indices, Has.Length.EqualTo(3));
		Assert.That(optimized.Positions, Has.Length.EqualTo(3));
		Assert.That(DescribeTriangles(optimized), Is.EquivalentTo([DescribeTriangle(P0, P1, P2)]));
	}

	[Test]
	public void Optimize_KeepsAnAllDegenerateMeshImportable()
	{
		var geometry = new MeshGeometry(
			[P0, P1],
			null,
			null,
			null,
			null,
			null,
			[0, 1, 1]);

		var optimized = MeshOptimization.Optimize(geometry);

		Assert.That(optimized.Indices, Is.Not.Empty);
		Assert.That(optimized.Positions, Is.Not.Empty);
	}

	[Test]
	public void Optimize_MovesSkinInfluencesWithTheirVertex()
	{
		var positions = new[] { P0, P1, P2, P3 };
		var boneIndices = new uint[]
		{
			10, 0, 0, 0,
			11, 0, 0, 0,
			12, 0, 0, 0,
			13, 0, 0, 0
		};
		var boneWeights = new float[]
		{
			1.0f, 0.0f, 0.0f, 0.0f,
			1.0f, 0.0f, 0.0f, 0.0f,
			1.0f, 0.0f, 0.0f, 0.0f,
			1.0f, 0.0f, 0.0f, 0.0f
		};

		var geometry = new MeshGeometry(
			positions,
			null,
			null,
			null,
			boneIndices,
			boneWeights,
			[2, 3, 1, 0, 1, 2]);

		var optimized = MeshOptimization.Optimize(geometry);

		Assert.That(optimized.BoneIndices, Is.Not.Null);
		Assert.That(optimized.BoneWeights, Is.Not.Null);
		Assert.That(optimized.BoneIndices, Has.Length.EqualTo(optimized.Positions.Length * Mesh.InfluencesPerVertex));

		var expected = new Dictionary<Vector4, uint>
		{
			[P0] = 10, [P1] = 11, [P2] = 12, [P3] = 13
		};
		for (var vertex = 0; vertex < optimized.Positions.Length; vertex++)
		{
			Assert.That(
				optimized.BoneIndices![vertex * Mesh.InfluencesPerVertex],
				Is.EqualTo(expected[optimized.Positions[vertex]]),
				$"Vertex {vertex} lost its bone influence.");
		}
	}

	[Test]
	public void Optimize_LeavesGeometryUntouchedWhenItIsNotATriangleList()
	{
		var geometry = new MeshGeometry([P0, P1], null, null, null, null, null, [0, 1]);

		Assert.That(MeshOptimization.Optimize(geometry), Is.SameAs(geometry));
	}

	/// <summary>
	/// Describes each triangle by its corner attributes rather than its indices, so two meshes compare
	/// equal when they draw the same thing. Corners are rotated into a canonical order because the
	/// optimizer preserves winding but not which corner comes first.
	/// </summary>
	private static IReadOnlyList<string> DescribeTriangles(MeshGeometry geometry)
	{
		var triangles = new List<string>(geometry.Indices.Length / 3);
		for (var i = 0; i < geometry.Indices.Length; i += 3)
		{
			var corners = new string[3];
			for (var corner = 0; corner < 3; corner++)
			{
				var vertex = (int)geometry.Indices[i + corner];
				corners[corner] =
					$"{geometry.Positions[vertex]}|{geometry.Normals?[vertex]}|{geometry.Uvs?[vertex]}|{geometry.Tangents?[vertex]}";
			}

			triangles.Add(Canonicalize(corners));
		}

		return triangles;
	}

	private static string DescribeTriangle(Vector4 a, Vector4 b, Vector4 c)
	{
		return Canonicalize([$"{a}|||", $"{b}|||", $"{c}|||"]);
	}

	private static string Canonicalize(string[] corners)
	{
		var first = 0;
		for (var corner = 1; corner < corners.Length; corner++)
		{
			if (string.CompareOrdinal(corners[corner], corners[first]) < 0)
			{
				first = corner;
			}
		}

		return string.Join(" / ", Enumerable.Range(0, corners.Length).Select(offset => corners[(first + offset) % corners.Length]));
	}
}
