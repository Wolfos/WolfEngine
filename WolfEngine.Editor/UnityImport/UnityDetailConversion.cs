namespace WolfEngine.Editor.UnityImport;

/// <summary>How well one Unity detail prototype's painted density is explained by the terrain's layers.</summary>
/// <param name="Densities">Plants per square metre the prototype grows on each layer where that layer is fully painted.</param>
/// <param name="PaintedPlants">Plants Unity's detail map places across the terrain.</param>
/// <param name="FittedPlants">Plants the fitted densities place across the terrain.</param>
/// <param name="RelativeError">
/// Root mean square difference between painted and fitted density per block of <see cref="UnityDetailConversion.BlockSizeMeters"/>,
/// as a fraction of the painted root mean square: 0 when layers explain where plants were painted, 1 when the fit
/// explains none of it.
/// </param>
public sealed record UnityDetailFit(
	float[] Densities,
	double PaintedPlants,
	double FittedPlants,
	float RelativeError);

/// <summary>
/// Turns Unity's painted detail maps into densities per terrain layer. WolfEngine grows detail from layers rather than
/// storing a map per plant type, so each prototype's density is fitted as a weighted sum of the layer weights:
/// <c>density(block) ≈ Σ weight(layer, block) × density(layer)</c>, by non-negative least squares over blocks of detail
/// cells. Unity's cells are a few centimetres across and hold a plant or none, so the fit runs on blocks large enough to
/// hold a density.
/// Where the painting followed the layers the fit reproduces it; patches painted independently of them are lost, which
/// <see cref="UnityDetailFit.RelativeError"/> measures.
/// </summary>
public static class UnityDetailConversion
{
	private const int SolverIterations = 400;

	/// <summary>Width of the square blocks densities are measured over, in metres.</summary>
	public const float BlockSizeMeters = 2.0f;

	/// <param name="alphamaps">One plane per layer of <paramref name="alphamapResolution"/> squared bytes, rows along Z.</param>
	/// <param name="details">One prototype's plane of <paramref name="detailResolution"/> squared values, rows along Z.</param>
	/// <param name="densityPerValue">Plants per square metre one unit of a detail map value stands for.</param>
	public static UnityDetailFit Fit(
		ReadOnlySpan<byte> alphamaps,
		int alphamapResolution,
		int layerCount,
		ReadOnlySpan<ushort> details,
		int detailResolution,
		float densityPerValue,
		float terrainArea)
	{
		if (alphamapResolution <= 0 || detailResolution <= 0 || layerCount <= 0)
		{
			return new UnityDetailFit(new float[Math.Max(layerCount, 0)], 0.0, 0.0, 0.0f);
		}

		var planeSize = alphamapResolution * alphamapResolution;
		if (alphamaps.Length < planeSize * layerCount || details.Length < detailResolution * detailResolution)
		{
			throw new ArgumentException("The alphamaps or detail map are smaller than their resolution says.");
		}

		// Each block's mean density and mean layer weights, from every detail cell in it.
		var cellSize = MathF.Sqrt(MathF.Max(terrainArea, 0.0f)) / detailResolution;
		var blockCells = Math.Clamp((int)MathF.Round(BlockSizeMeters / MathF.Max(cellSize, 1e-6f)), 1, detailResolution);
		var blocksPerSide = (detailResolution + blockCells - 1) / blockCells;
		var blockCount = blocksPerSide * blocksPerSide;
		var blockDensities = new double[blockCount];
		var blockWeights = new double[blockCount * layerCount];
		var blockCellCounts = new int[blockCount];
		var weights = new double[layerCount];
		var paintedSum = 0.0;
		for (var z = 0; z < detailResolution; z++)
		{
			for (var x = 0; x < detailResolution; x++)
			{
				var block = ((z / blockCells) * blocksPerSide) + (x / blockCells);
				var density = details[(z * detailResolution) + x] * (double)densityPerValue;
				SampleWeights(alphamaps, alphamapResolution, layerCount, (x + 0.5) / detailResolution, (z + 0.5) / detailResolution, weights);
				blockDensities[block] += density;
				blockCellCounts[block]++;
				paintedSum += density;
				for (var i = 0; i < layerCount; i++)
				{
					blockWeights[(block * layerCount) + i] += weights[i];
				}
			}
		}

		// Normal equations over blocks: gram = Σ w wᵀ and moment = Σ w d, with w a block's mean weights and d its density.
		var gram = new double[layerCount, layerCount];
		var moment = new double[layerCount];
		var paintedSquares = 0.0;
		for (var block = 0; block < blockCount; block++)
		{
			var cells = blockCellCounts[block];
			if (cells == 0)
			{
				continue;
			}

			var density = blockDensities[block] / cells;
			paintedSquares += density * density;
			for (var i = 0; i < layerCount; i++)
			{
				weights[i] = blockWeights[(block * layerCount) + i] / cells;
			}

			for (var i = 0; i < layerCount; i++)
			{
				if (weights[i] == 0.0)
				{
					continue;
				}

				moment[i] += weights[i] * density;
				for (var j = 0; j < layerCount; j++)
				{
					gram[i, j] += weights[i] * weights[j];
				}
			}
		}

		var solution = SolveNonNegative(gram, moment, layerCount);

		// The residual follows from the normal equations: Σ (d - wᵀx)² = Σ d² - 2 xᵀm + xᵀGx.
		var residual = paintedSquares;
		for (var i = 0; i < layerCount; i++)
		{
			residual -= 2.0 * solution[i] * moment[i];
			for (var j = 0; j < layerCount; j++)
			{
				residual += solution[i] * gram[i, j] * solution[j];
			}
		}

		var cellArea = terrainArea / ((double)detailResolution * detailResolution);
		var fittedSum = 0.0;
		for (var block = 0; block < blockCount; block++)
		{
			for (var i = 0; i < layerCount; i++)
			{
				// The block's summed weights times its cell area is its mean weight times its area.
				fittedSum += blockWeights[(block * layerCount) + i] * solution[i];
			}
		}

		var relativeError = paintedSquares > 0.0
			? (float)Math.Sqrt(Math.Max(residual, 0.0) / paintedSquares)
			: 0.0f;

		return new UnityDetailFit(
			solution.Select(value => (float)value).ToArray(),
			paintedSum * cellArea,
			fittedSum * cellArea,
			relativeError);
	}

	/// <summary>
	/// Layer weights at a normalised terrain position, bilinearly from the alphamaps and normalised to sum to one.
	/// </summary>
	private static void SampleWeights(
		ReadOnlySpan<byte> alphamaps,
		int resolution,
		int layerCount,
		double u,
		double v,
		double[] weights)
	{
		var planeSize = resolution * resolution;
		var sampleX = Math.Clamp((u * resolution) - 0.5, 0.0, resolution - 1);
		var sampleZ = Math.Clamp((v * resolution) - 0.5, 0.0, resolution - 1);
		var x0 = (int)sampleX;
		var z0 = (int)sampleZ;
		var x1 = Math.Min(x0 + 1, resolution - 1);
		var z1 = Math.Min(z0 + 1, resolution - 1);
		var tx = sampleX - x0;
		var tz = sampleZ - z0;
		var sum = 0.0;
		for (var layer = 0; layer < layerCount; layer++)
		{
			var plane = layer * planeSize;
			var a = alphamaps[plane + (z0 * resolution) + x0];
			var b = alphamaps[plane + (z0 * resolution) + x1];
			var c = alphamaps[plane + (z1 * resolution) + x0];
			var d = alphamaps[plane + (z1 * resolution) + x1];
			var top = a + ((b - a) * tx);
			var bottom = c + ((d - c) * tx);
			weights[layer] = top + ((bottom - top) * tz);
			sum += weights[layer];
		}

		for (var layer = 0; layer < layerCount; layer++)
		{
			weights[layer] = sum > 0.0 ? weights[layer] / sum : 0.0;
		}
	}

	/// <summary>
	/// Minimises xᵀGx - 2mᵀx subject to x ≥ 0 by cyclic coordinate descent, which converges for the positive
	/// semi-definite gram matrices layer weights give. A layer no cell paints has a zero diagonal and stays zero.
	/// </summary>
	private static double[] SolveNonNegative(double[,] gram, double[] moment, int count)
	{
		var solution = new double[count];
		for (var iteration = 0; iteration < SolverIterations; iteration++)
		{
			var largestChange = 0.0;
			for (var i = 0; i < count; i++)
			{
				if (gram[i, i] <= 1e-12)
				{
					continue;
				}

				var gradient = moment[i];
				for (var j = 0; j < count; j++)
				{
					if (j != i)
					{
						gradient -= gram[i, j] * solution[j];
					}
				}

				var updated = Math.Max(0.0, gradient / gram[i, i]);
				largestChange = Math.Max(largestChange, Math.Abs(updated - solution[i]));
				solution[i] = updated;
			}

			if (largestChange < 1e-7)
			{
				break;
			}
		}

		return solution;
	}
}
