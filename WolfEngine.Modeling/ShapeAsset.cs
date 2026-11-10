using System.Numerics;

namespace WolfEngine.Modeling;

/// <summary>Per-asset settings that steer the compile and the imported result.</summary>
public sealed class ShapeCompileSettings
{
	public const int DefaultReferenceResolution = 128;
	public const int MinReferenceResolution = 16;
	public const int MaxReferenceResolution = 384;
	public const int DefaultTriangleBudget = 5000;
	public const float DefaultErrorTolerance = 0.005f;
	public const float DefaultCreaseAngleDegrees = 40.0f;

	/// <summary>Grid cells along the longest axis of the shape's bounds when sampling the dense reference surface.</summary>
	public int ReferenceResolution { get; set; } = DefaultReferenceResolution;

	/// <summary>Triangles the compiled mesh should stay within.</summary>
	public int TriangleBudget { get; set; } = DefaultTriangleBudget;

	/// <summary>Largest distance, in shape units, the compiled mesh may stray from the reference surface.</summary>
	public float ErrorTolerance { get; set; } = DefaultErrorTolerance;

	/// <summary>Normals split where neighbouring faces meet at more than this angle.</summary>
	public float CreaseAngleDegrees { get; set; } = DefaultCreaseAngleDegrees;

	/// <summary>The material the imported mesh uses, or empty for the engine default.</summary>
	public Guid MaterialId { get; set; }

	public ShapeCompileSettings Clone()
	{
		return new ShapeCompileSettings
		{
			ReferenceResolution = ReferenceResolution,
			TriangleBudget = TriangleBudget,
			ErrorTolerance = ErrorTolerance,
			CreaseAngleDegrees = CreaseAngleDegrees,
			MaterialId = MaterialId
		};
	}

	public int GetClampedReferenceResolution()
	{
		return Math.Clamp(ReferenceResolution, MinReferenceResolution, MaxReferenceResolution);
	}
}

/// <summary>
/// A compiled triangle mesh. It is never edited after it is built, so undo steps and the preview can share one
/// instance instead of copying it.
/// <para>
/// Triangles wind so that <c>cross(b - a, c - a)</c> points out of the surface, which is the engine's convention.
/// </para>
/// </summary>
public sealed class ShapeMesh
{
	public ShapeMesh(Vector3[] positions, Vector3[] normals, uint[] indices)
	{
		ArgumentNullException.ThrowIfNull(positions);
		ArgumentNullException.ThrowIfNull(normals);
		ArgumentNullException.ThrowIfNull(indices);
		if (normals.Length != positions.Length)
		{
			throw new ArgumentException("Every vertex needs a normal.", nameof(normals));
		}

		if (indices.Length % 3 != 0)
		{
			throw new ArgumentException("Indices must describe whole triangles.", nameof(indices));
		}

		Positions = positions;
		Normals = normals;
		Indices = indices;
	}

	public static ShapeMesh Empty { get; } = new([], [], []);

	public Vector3[] Positions { get; }
	public Vector3[] Normals { get; }
	public uint[] Indices { get; }

	public int VertexCount => Positions.Length;
	public int TriangleCount => Indices.Length / 3;
	public bool IsEmpty => Indices.Length == 0;
}

/// <summary>
/// Everything a <c>.shape</c> source file holds. The compiled mesh is authoritative, not a cache: each compile starts
/// from the previous mesh, so it cannot be rebuilt from the tree alone and has to be saved with it.
/// </summary>
public sealed class ShapeAsset
{
	public ShapeTree Tree { get; set; } = new();
	public ShapeCompileSettings Settings { get; set; } = new();

	/// <summary>The last compiled mesh, or null when the shape has never been compiled.</summary>
	public ShapeMesh? Mesh { get; set; }

	/// <summary><see cref="ShapeFile.ComputeSourceHash"/> of the tree and settings <see cref="Mesh"/> was compiled from.</summary>
	public ulong MeshSourceHash { get; set; }

	public static ShapeAsset CreateDefault()
	{
		var asset = new ShapeAsset();
		asset.Tree.Root.Children.Add(ShapeNode.CreatePrimitive(ShapePrimitiveKind.RoundedBox));
		return asset;
	}
}
