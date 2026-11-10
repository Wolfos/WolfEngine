using System.Numerics;

namespace WolfEngine.Modeling;

/// <summary>Where a regular grid of distance samples sits in shape space.</summary>
public readonly record struct SdfGridLayout(Vector3 Origin, float CellSize, int SizeX, int SizeY, int SizeZ)
{
	/// <summary>The most samples one grid may hold, which bounds the memory a single compile can take.</summary>
	public const long MaxSampleCount = 400L * 400L * 400L;

	public long SampleCount => (long)SizeX * SizeY * SizeZ;

	public int GetIndex(int x, int y, int z)
	{
		return (z * SizeY + y) * SizeX + x;
	}

	public Vector3 GetPosition(int x, int y, int z)
	{
		return Origin + new Vector3(x, y, z) * CellSize;
	}

	/// <summary>
	/// A grid covering the bounds with <paramref name="resolution"/> cells along the longest axis, padded by two
	/// cells on every side so the surface never touches the grid's edge and always closes.
	/// </summary>
	public static SdfGridLayout Create(ShapeBounds bounds, int resolution)
	{
		if (bounds.IsEmpty)
		{
			throw new ArgumentException("Cannot sample an empty shape.", nameof(bounds));
		}

		const int padding = 2;
		var size = bounds.Size;
		var longest = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
		var cellSize = MathF.Max(longest, 1.0e-4f) / Math.Max(1, resolution);
		var origin = bounds.Min - new Vector3(cellSize * padding);
		var sizeX = (int)MathF.Ceiling(size.X / cellSize) + padding * 2 + 1;
		var sizeY = (int)MathF.Ceiling(size.Y / cellSize) + padding * 2 + 1;
		var sizeZ = (int)MathF.Ceiling(size.Z / cellSize) + padding * 2 + 1;
		var layout = new SdfGridLayout(origin, cellSize, sizeX, sizeY, sizeZ);
		if (layout.SampleCount > MaxSampleCount)
		{
			throw new InvalidOperationException($"A {sizeX}x{sizeY}x{sizeZ} grid is larger than a compile allows.");
		}

		return layout;
	}
}

/// <summary>Signed distances sampled at every point of a <see cref="SdfGridLayout"/>, X fastest.</summary>
public sealed class SdfGrid
{
	public SdfGrid(SdfGridLayout layout, float[] distances)
	{
		ArgumentNullException.ThrowIfNull(distances);
		if (distances.Length != layout.SampleCount)
		{
			throw new ArgumentException("The distances do not fill the grid.", nameof(distances));
		}

		Layout = layout;
		Distances = distances;
	}

	public SdfGridLayout Layout { get; }
	public float[] Distances { get; }

	public float this[int x, int y, int z] => Distances[Layout.GetIndex(x, y, z)];
}

/// <summary>Evaluates a shape program at every point of a grid.</summary>
public interface ISdfGridSampler
{
	/// <summary>A short name for reports, such as "gpu" or "cpu".</summary>
	string Name { get; }

	SdfGrid Sample(ShapeSdfProgram program, SdfGridLayout layout);
}

/// <summary>The reference sampler: runs the program on the CPU, one Z slice per worker.</summary>
public sealed class CpuSdfGridSampler : ISdfGridSampler
{
	public string Name => "cpu";

	public SdfGrid Sample(ShapeSdfProgram program, SdfGridLayout layout)
	{
		ArgumentNullException.ThrowIfNull(program);

		var distances = new float[layout.SampleCount];
		Parallel.For(0, layout.SizeZ, z =>
		{
			for (var y = 0; y < layout.SizeY; y++)
			{
				for (var x = 0; x < layout.SizeX; x++)
				{
					distances[layout.GetIndex(x, y, z)] = program.Evaluate(layout.GetPosition(x, y, z));
				}
			}
		});

		return new SdfGrid(layout, distances);
	}
}
