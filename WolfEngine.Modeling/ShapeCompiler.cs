using System.Diagnostics;

namespace WolfEngine.Modeling;

/// <summary>Measurements of one compile, for the editor's metrics readout and for automation.</summary>
public sealed record ShapeCompileStats(
	string Sampler,
	int GridSizeX,
	int GridSizeY,
	int GridSizeZ,
	float CellSize,
	int InstructionCount,
	int VertexCount,
	int TriangleCount,
	double SampleMilliseconds,
	double ExtractMilliseconds,
	double TotalMilliseconds,
	float MinSampledDistance,
	float MaxSampledDistance);

public sealed record ShapeCompileResult(ShapeMesh Mesh, ShapeCompileStats Stats);

/// <summary>
/// Turns a shape into its compiled mesh.
/// <para>
/// Milestone 1 stops at the dense reference surface: the compiled mesh is the surface extracted from a distance grid.
/// The remesher (milestone 2) will replace that last step with a sizing-field-driven remesh warm-started from the
/// previous mesh, which is why <see cref="Compile"/> already takes one.
/// </para>
/// </summary>
public sealed class ShapeCompiler
{
	private readonly ISdfGridSampler _sampler;

	public ShapeCompiler(ISdfGridSampler sampler)
	{
		_sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
	}

	public ShapeCompileResult Compile(ShapeSdfProgram program, ShapeCompileSettings settings, ShapeMesh? previous)
	{
		ArgumentNullException.ThrowIfNull(program);
		ArgumentNullException.ThrowIfNull(settings);
		_ = previous;

		var total = Stopwatch.StartNew();
		if (program.Bounds.IsEmpty)
		{
			return new ShapeCompileResult(
				ShapeMesh.Empty,
				new ShapeCompileStats(_sampler.Name, 0, 0, 0, 0.0f, program.Instructions.Length, 0, 0, 0.0, 0.0, total.Elapsed.TotalMilliseconds, 0.0f, 0.0f));
		}

		var layout = SdfGridLayout.Create(program.Bounds, settings.GetClampedReferenceResolution());

		var sampleTimer = Stopwatch.StartNew();
		var grid = _sampler.Sample(program, layout);
		var sampleMilliseconds = sampleTimer.Elapsed.TotalMilliseconds;

		// The sampled range is a cheap sanity check: a grid with no negative sample holds no surface at all.
		var minDistance = float.MaxValue;
		var maxDistance = float.MinValue;
		foreach (var distance in grid.Distances)
		{
			minDistance = MathF.Min(minDistance, distance);
			maxDistance = MathF.Max(maxDistance, distance);
		}

		var extractTimer = Stopwatch.StartNew();
		var mesh = SurfaceNets.Extract(grid);
		var extractMilliseconds = extractTimer.Elapsed.TotalMilliseconds;

		return new ShapeCompileResult(
			mesh,
			new ShapeCompileStats(
				_sampler.Name,
				layout.SizeX,
				layout.SizeY,
				layout.SizeZ,
				layout.CellSize,
				program.Instructions.Length,
				mesh.VertexCount,
				mesh.TriangleCount,
				sampleMilliseconds,
				extractMilliseconds,
				total.Elapsed.TotalMilliseconds,
				minDistance,
				maxDistance));
	}
}
