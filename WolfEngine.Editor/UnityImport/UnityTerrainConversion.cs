using System.Numerics;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.UnityImport;

/// <summary>
/// Turns an exported Unity terrain's maps and trees into WolfEngine terrain data. Free of the project and the asset
/// pipeline, so it can be tested on its own.
/// </summary>
/// <remarks>
/// A Unity terrain spans from its position along +X and +Z; a WolfEngine terrain is centred on its entity. Both store map
/// rows along +Z and sample them at texel centres, so the maps carry over row for row, and the entity sits at the Unity
/// terrain's centre (<see cref="GetEntityPosition"/>).
/// </remarks>
public static class UnityTerrainConversion
{
	/// <summary>WolfEngine layer maps hold this many layers per texel.</summary>
	public const int LayersPerTexel = 4;

	public static Vector3 GetEntityPosition(UnityTerrainExport terrain)
	{
		var size = terrain.GetSize();

		return terrain.GetPosition() + new Vector3(size.X * 0.5f, 0.0f, size.Z * 0.5f);
	}

	/// <summary>
	/// Sizes the terrain like Unity's and draws its nearest level at the heightmap's own spacing, as Unity does close up;
	/// the component's default draws a quarter of that, smoothing away the detail the heightmap holds. Ray tracing gets
	/// half the drawn resolution.
	/// </summary>
	public static void ApplyTo(ref TerrainComponent component, UnityTerrainExport terrain)
	{
		var size = terrain.GetSize();
		component.WorldSizeMeters = new Vector2(size.X, size.Z);
		component.HeightScaleMeters = size.Y;

		var sampleSpacing = size.X / Math.Max(terrain.HeightmapResolution - 1, 1);
		if (sampleSpacing > 0.0f)
		{
			var quads = (int)MathF.Round(component.GetResolvedChunkSizeMeters() / sampleSpacing);
			component.Lod0ResolutionInQuads = Math.Clamp(quads, 2, 1024);
			component.RayTracingResolutionInQuads = Math.Clamp(quads / 2, 1, 256);
		}
	}

	/// <summary>
	/// Builds the terrain asset. Each layer map texel keeps its <see cref="LayersPerTexel"/> heaviest Unity layers, with
	/// their weights renormalised; Unity layer <c>i</c> becomes layer set entry <c>i</c>, stored as index <c>i + 1</c>
	/// because stored index zero means the automatic layer.
	/// </summary>
	/// <param name="discardedWeight">The mean weight per texel that did not fit, from 0 to 1.</param>
	public static TerrainAsset CreateTerrainAsset(
		string name,
		UnityTerrainExport terrain,
		byte[] heights,
		byte[] alphamaps,
		out float discardedWeight)
	{
		ArgumentNullException.ThrowIfNull(heights);
		ArgumentNullException.ThrowIfNull(alphamaps);

		var heightResolution = terrain.HeightmapResolution;
		var heightmap = new Texture(
			$"{name}:height",
			heightResolution,
			heightResolution,
			false,
			TextureFormat.R16Unorm,
			[new TextureMipData(heightResolution, heightResolution, heights.ToArray())]);

		var (indices, weights) = PackLayerMaps(alphamaps, terrain.AlphamapResolution, terrain.Layers.Count, out discardedWeight);
		var layerResolution = terrain.AlphamapResolution;
		var indexMip = new TextureMipData(layerResolution, layerResolution, indices);
		var weightMip = new TextureMipData(layerResolution, layerResolution, weights);
		var mips = TerrainLayerMapUtility.GenerateLayerMipChain(indexMip, weightMip);

		return new TerrainAsset(
			name,
			heightmap,
			new Texture($"{name}:layer_indices", layerResolution, layerResolution, false, TextureFormat.Rgba8Uint, mips.Indices),
			new Texture($"{name}:layer_weights", layerResolution, layerResolution, false, TextureFormat.Rgba8Unorm, mips.Weights));
	}

	/// <summary>Packs per-layer weight planes into four-layer index and weight texels.</summary>
	public static (byte[] Indices, byte[] Weights) PackLayerMaps(
		byte[] alphamaps,
		int resolution,
		int layerCount,
		out float discardedWeight)
	{
		if (layerCount > TerrainLayerSet.MaxLayerCount - 1)
		{
			throw new InvalidDataException($"The terrain has {layerCount} layers; WolfEngine terrains hold at most {TerrainLayerSet.MaxLayerCount - 1}.");
		}

		var planeSize = resolution * resolution;
		var indices = new byte[planeSize * 4];
		var weights = new byte[planeSize * 4];
		Span<int> heaviest = stackalloc int[LayersPerTexel];
		var discardedTotal = 0L;
		var weightTotal = 0L;
		for (var pixel = 0; pixel < planeSize; pixel++)
		{
			heaviest.Fill(-1);
			var pixelTotal = 0;
			for (var layer = 0; layer < layerCount; layer++)
			{
				var weight = alphamaps[(layer * planeSize) + pixel];
				pixelTotal += weight;
				InsertHeaviest(heaviest, alphamaps, planeSize, pixel, layer, weight);
			}

			var offset = pixel * 4;
			var kept = 0;
			for (var slot = 0; slot < LayersPerTexel; slot++)
			{
				var layer = heaviest[slot];
				if (layer < 0)
				{
					continue;
				}

				var weight = alphamaps[(layer * planeSize) + pixel];
				indices[offset + slot] = (byte)(layer + 1);
				weights[offset + slot] = weight;
				kept += weight;
			}

			discardedTotal += pixelTotal - kept;
			weightTotal += pixelTotal;
			TerrainLayerMapUtility.NormalizePixel(indices, weights, pixel);
		}

		discardedWeight = weightTotal > 0 ? discardedTotal / (float)weightTotal : 0.0f;

		return (indices, weights);
	}

	/// <summary>Keeps the slots sorted heaviest first, so a lighter layer never displaces a heavier one.</summary>
	private static void InsertHeaviest(Span<int> heaviest, byte[] alphamaps, int planeSize, int pixel, int layer, byte weight)
	{
		if (weight == 0) return;

		for (var slot = 0; slot < heaviest.Length; slot++)
		{
			var current = heaviest[slot];
			if (current >= 0 && alphamaps[(current * planeSize) + pixel] >= weight)
			{
				continue;
			}

			for (var move = heaviest.Length - 1; move > slot; move--)
			{
				heaviest[move] = heaviest[move - 1];
			}

			heaviest[slot] = layer;
			return;
		}
	}

	/// <summary>A Unity terrain tree as a placed foliage instance in the terrain entity's local space.</summary>
	public static FoliageInstance CreateTreeInstance(UnityTreeExport tree, Vector3 terrainSize, int typeIndex)
	{
		var normalized = UnitySpace.ToVector3(tree.Position);
		var position = new Vector3(
			(normalized.X - 0.5f) * terrainSize.X,
			normalized.Y * terrainSize.Y,
			(normalized.Z - 0.5f) * terrainSize.Z);

		return new FoliageInstance(
			position,
			UnitySpace.ToTreeRotation(tree.Rotation),
			tree.HeightScale,
			typeIndex,
			tree.WidthScale);
	}
}
