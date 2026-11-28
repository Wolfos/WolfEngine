using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WolfEngine.Rendering;

/// <summary>
/// How one detail type grows on one terrain layer, as detail generation reads it, indexed by layer and then type.
/// A density of zero means the layer does not grow the type. Mirrors <c>GpuDetailEntry</c> in
/// foliage_detail_generate.compute.slang.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuDetailEntry
{
	/// <summary>x: density per square metre, y: min height scale, z: max height scale, w: align to normal.</summary>
	public Vector4 DensityScale;

	/// <summary>x: min width ratio, y: max width ratio, z: clump size in metres (zero for none), w: clump strength.</summary>
	public Vector4 WidthClump;

	/// <summary>x: steepest slope in degrees, y: lowest height, z: highest height, w: unused.</summary>
	public Vector4 Limits;
}

/// <summary>
/// One detail type of a terrain: the foliage type it draws, and the grid of candidate plants generated around the
/// camera. Candidates sit one per grid cell of <see cref="Spacing"/>, jittered inside it, so the grid holds the
/// densest any layer grows the type. Tiles of <see cref="TerrainDetailRuntime.TileCandidatesPerAxis"/> squared cells
/// are one chunk each, and <see cref="GridDimension"/> squared tiles around the camera cover the type's distance.
/// </summary>
/// <param name="ChunkStart">The type's first chunk in the terrain's chunk buffer; its chunks follow in tile-slot order.</param>
/// <param name="Seed">Decorrelates the type's random values from other types', from its asset id.</param>
internal sealed record TerrainDetailTypeRuntime(
	FoliageTypeRuntime Type,
	int TypeIndex,
	float MaxDensity,
	float Spacing,
	float Distance,
	float FadeDistance,
	int GridDimension,
	int ChunkStart,
	uint Seed)
{
	public float TileSize => Spacing * TerrainDetailRuntime.TileCandidatesPerAxis;

	public int ChunkCount => GridDimension * GridDimension;

	public int CandidateCount => ChunkCount * TerrainDetailRuntime.CandidatesPerTile;
}

/// <summary>
/// A terrain's detail foliage, resolved for generation: every foliage type its layers' biomes grow, each with the
/// candidate grid it is generated on, and the entry table generation looks density and growth up in. Nothing is stored
/// per plant; the GPU regenerates plants near the camera every frame into chunks in the same packed form as placed
/// foliage, which the foliage cull then draws. Immutable once built; <see cref="TerrainDetailRuntimeCache"/> builds a
/// new one when the layer set, a biome or a type changes. See docs/foliage.md.
/// </summary>
internal sealed class TerrainDetailRuntime : IFoliageCullSource
{
	/// <summary>Candidate cells along each side of a tile.</summary>
	public const int TileCandidatesPerAxis = 8;

	/// <summary>Candidates per tile: one per thread of a generation and a cull thread group.</summary>
	public const int CandidatesPerTile = TileCandidatesPerAxis * TileCandidatesPerAxis;

	/// <summary>
	/// Most candidates a terrain generates per frame across its types. Past it, every type's distance shrinks by the
	/// same factor.
	/// </summary>
	public const int MaxCandidateCount = 1 << 20;

	/// <summary>Highest density, in plants per square metre, an entry is generated at.</summary>
	public const float MaxDensity = 64.0f;

	public TerrainDetailRuntime(
		TerrainLayerSet layerSet,
		IReadOnlyList<FoliageTypeRuntime> types,
		IReadOnlyList<Guid> typeIds,
		GpuDetailEntry[] entries,
		int layerCount)
	{
		ArgumentNullException.ThrowIfNull(layerSet);
		ArgumentNullException.ThrowIfNull(types);
		ArgumentNullException.ThrowIfNull(typeIds);
		ArgumentNullException.ThrowIfNull(entries);

		LayerSet = layerSet;
		LayerCount = layerCount;
		Entries = entries;
		Types = types.ToArray();

		var maxDensities = new float[types.Count];
		for (var layer = 0; layer < layerCount; layer++)
		{
			for (var typeIndex = 0; typeIndex < types.Count; typeIndex++)
			{
				maxDensities[typeIndex] = MathF.Max(maxDensities[typeIndex], entries[(layer * types.Count) + typeIndex].DensityScale.X);
			}
		}

		// Candidates a type needs grow with the square of its distance, so one factor on every distance brings the total
		// under the limit.
		var requested = 0.0;
		for (var typeIndex = 0; typeIndex < types.Count; typeIndex++)
		{
			var distance = types[typeIndex].CullDistance;
			requested += maxDensities[typeIndex] * 4.0 * distance * distance;
		}

		DistanceScale = requested > MaxCandidateCount ? (float)Math.Sqrt(MaxCandidateCount / requested) : 1.0f;

		var detailTypes = new List<TerrainDetailTypeRuntime>();
		var chunkStart = 0;
		for (var typeIndex = 0; typeIndex < types.Count; typeIndex++)
		{
			var type = types[typeIndex];
			var maxDensity = maxDensities[typeIndex];
			var distance = type.CullDistance * DistanceScale;
			if (maxDensity <= 0.0f || distance <= 0.0f)
			{
				continue;
			}

			var spacing = 1.0f / MathF.Sqrt(maxDensity);
			var tileSize = spacing * TileCandidatesPerAxis;

			var gridDimension = GetGridDimension(distance, tileSize);
			var fadeDistance = Math.Clamp(type.Type.DetailFadeDistance, 0.0f, distance);
			var detailType = new TerrainDetailTypeRuntime(
				type,
				typeIndex,
				maxDensity,
				spacing,
				distance,
				fadeDistance,
				gridDimension,
				chunkStart,
				CreateSeed(typeIds[typeIndex]));
			detailTypes.Add(detailType);
			chunkStart += detailType.ChunkCount;
		}

		DetailTypes = detailTypes.ToArray();
		ChunkCount = chunkStart;
		CandidateCount = chunkStart * CandidatesPerTile;

		TypeCandidateCounts = new int[types.Count];
		foreach (var detailType in DetailTypes)
		{
			TypeCandidateCounts[detailType.TypeIndex] = detailType.CandidateCount;
			RendererInstanceCount += detailType.CandidateCount * detailType.Type.Renderers.Count;
		}
	}

	public TerrainLayerSet LayerSet { get; }

	/// <summary>Layers the entry table covers, which is the layer set's resolved count.</summary>
	public int LayerCount { get; }

	/// <summary>Every foliage type any layer's biome grows, indexed like the entry table's columns.</summary>
	public FoliageTypeRuntime[] Types { get; }

	/// <summary>The types that grow anywhere, each with its candidate grid.</summary>
	public TerrainDetailTypeRuntime[] DetailTypes { get; }

	/// <summary><see cref="LayerCount"/> rows of one entry per type.</summary>
	public GpuDetailEntry[] Entries { get; }

	/// <summary>
	/// What every type's distance was multiplied by to stay within <see cref="MaxCandidateCount"/>; 1 when it fits.
	/// </summary>
	public float DistanceScale { get; }

	public int ChunkCount { get; }

	public int CandidateCount { get; }

	/// <summary>Per type, how many candidates its grid holds: the most of it that can be visible at once.</summary>
	public int[] TypeCandidateCounts { get; }

	/// <summary>Every candidate's renderers added up: what drawing all of them at once would expand into.</summary>
	public int RendererInstanceCount { get; }

	IReadOnlyList<FoliageTypeRuntime?> IFoliageCullSource.Types => Types;

	int IFoliageCullSource.SlotCount => CandidateCount;

	int IFoliageCullSource.GetTypeSlotCount(int typeIndex) => TypeCandidateCounts[typeIndex];

	/// <summary>
	/// Tiles along each side of the window around the camera. The window has to reach the distance from wherever the camera
	/// sits inside its tile, so it is the camera's tile and enough whole tiles on each side to reach the distance from
	/// either edge of it.
	/// </summary>
	public static int GetGridDimension(float distance, float tileSize)
	{
		var tilesPerSide = (int)MathF.Ceiling(distance / tileSize);

		return (2 * tilesPerSide) + 1;
	}

	/// <summary>
	/// The tile, in tile coordinates of the type's grid, that the window of tiles around the camera starts at. Tile
	/// <c>(x, z)</c> covers terrain-local X and Z from <c>x * TileSize</c> to <c>(x + 1) * TileSize</c>.
	/// </summary>
	public static (int X, int Z) GetWindowOrigin(TerrainDetailTypeRuntime type, Vector3 localCamera)
	{
		var tileSize = type.TileSize;
		var half = type.GridDimension / 2;

		return (
			(int)MathF.Floor(localCamera.X / tileSize) - half,
			(int)MathF.Floor(localCamera.Z / tileSize) - half);
	}

	/// <summary>
	/// Where a tile lives in the type's chunks: tiles map onto slots modulo the grid dimension, so a tile keeps its
	/// chunk, and its plants their level-of-detail state, while the camera moves and it stays in the window.
	/// </summary>
	public static int GetTileSlot(TerrainDetailTypeRuntime type, int tileX, int tileZ)
	{
		var dimension = type.GridDimension;
		var slotX = ((tileX % dimension) + dimension) % dimension;
		var slotZ = ((tileZ % dimension) + dimension) % dimension;

		return (slotZ * dimension) + slotX;
	}

	private static uint CreateSeed(Guid typeId)
	{
		Span<byte> bytes = stackalloc byte[16];
		typeId.TryWriteBytes(bytes);
		var words = MemoryMarshal.Cast<byte, uint>(bytes);

		return words[0] ^ (words[1] * 0x9E3779B9u) ^ (words[2] * 0x85EBCA6Bu) ^ (words[3] * 0xC2B2AE35u);
	}
}

/// <summary>
/// Keeps one <see cref="TerrainDetailRuntime"/> per layer set, rebuilt when a layer's biome, a biome's entries, a
/// type's settings or a type's prefab change. Main thread only.
/// </summary>
internal sealed class TerrainDetailRuntimeCache
{
	private readonly ConditionalWeakTable<TerrainLayerSet, Entry> _entries = new();

	/// <returns>Null when no layer grows detail.</returns>
	public TerrainDetailRuntime? Resolve(TerrainLayerSet layerSet, RenderGraph? renderGraph)
	{
		ArgumentNullException.ThrowIfNull(layerSet);

		var signature = CreateSignature(layerSet);
		if (_entries.TryGetValue(layerSet, out var entry) && entry.Signature.AsSpan().SequenceEqual(signature))
		{
			return entry.Runtime;
		}

		var runtime = Build(layerSet, renderGraph);
		_entries.AddOrUpdate(layerSet, new Entry(runtime, signature));

		return runtime;
	}

	private static TerrainDetailRuntime? Build(TerrainLayerSet layerSet, RenderGraph? renderGraph)
	{
		var layerCount = layerSet.ResolvedLayerCount;
		var typeIndices = new Dictionary<Guid, int>();
		var types = new List<FoliageTypeRuntime>();
		var typeIds = new List<Guid>();
		var layerEntries = new List<(int Layer, int TypeIndex, FoliageBiomeEntry Entry)>();
		for (var layer = 0; layer < layerCount; layer++)
		{
			if (layerSet.Layers[layer].Biome.Asset is not { } biome)
			{
				continue;
			}

			foreach (var entry in biome.Entries)
			{
				if (entry is null || entry.Type.Asset is not { } type || float.IsFinite(entry.Density) == false || entry.Density <= 0.0f)
				{
					continue;
				}

				if (typeIndices.TryGetValue(entry.Type.NodeId, out var typeIndex) == false)
				{
					if (FoliageTypeRuntime.TryCreate(type, renderGraph) is not { } typeRuntime)
					{
						continue;
					}

					typeIndex = types.Count;
					types.Add(typeRuntime);
					typeIds.Add(entry.Type.NodeId);
					typeIndices.Add(entry.Type.NodeId, typeIndex);
				}

				layerEntries.Add((layer, typeIndex, entry));
			}
		}

		if (types.Count == 0)
		{
			return null;
		}

		var entries = new GpuDetailEntry[layerCount * types.Count];
		foreach (var (layer, typeIndex, entry) in layerEntries)
		{
			// A biome naming a type twice grows both, so their densities add; the rest of the first entry is kept.
			ref var target = ref entries[(layer * types.Count) + typeIndex];
			var density = Math.Clamp(target.DensityScale.X + entry.Density, 0.0f, TerrainDetailRuntime.MaxDensity);
			if (target.DensityScale.X > 0.0f)
			{
				target.DensityScale.X = density;
				continue;
			}

			var minHeightScale = MathF.Max(0.0f, entry.MinHeightScale);
			var minWidthRatio = MathF.Max(0.0f, entry.MinWidthRatio);
			target = new GpuDetailEntry
			{
				DensityScale = new Vector4(
					density,
					minHeightScale,
					MathF.Max(minHeightScale, entry.MaxHeightScale),
					Math.Clamp(entry.AlignToNormal, 0.0f, 1.0f)),
				WidthClump = new Vector4(
					minWidthRatio,
					MathF.Max(minWidthRatio, entry.MaxWidthRatio),
					MathF.Max(0.0f, entry.ClumpSizeMeters),
					Math.Clamp(entry.ClumpStrength, 0.0f, 1.0f)),
				Limits = new Vector4(
					Math.Clamp(entry.MaxSlopeDegrees, 0.0f, 90.0f),
					ToFiniteLimit(entry.MinHeight, float.MinValue),
					ToFiniteLimit(entry.MaxHeight, float.MaxValue),
					0.0f)
			};
		}

		return new TerrainDetailRuntime(layerSet, types, typeIds, entries, layerCount);
	}

	private static float ToFiniteLimit(float value, float fallback)
	{
		if (float.IsNaN(value)) return fallback;

		return Math.Clamp(value, -1.0e30f, 1.0e30f);
	}

	/// <summary>Everything a rebuild depends on, compared by reference and value.</summary>
	private static object?[] CreateSignature(TerrainLayerSet layerSet)
	{
		var signature = new List<object?> { layerSet.ResolvedLayerCount };
		for (var layer = 0; layer < layerSet.ResolvedLayerCount; layer++)
		{
			var biome = layerSet.Layers[layer].Biome.Asset;
			signature.Add(biome);
			if (biome is null)
			{
				continue;
			}

			signature.Add(biome.Entries.Count);
			foreach (var entry in biome.Entries)
			{
				var type = entry?.Type.Asset;
				signature.Add(entry);
				signature.Add(type);
				signature.Add(type?.Prefab.Asset);
				signature.Add(type?.CullDistance);
				signature.Add(type?.ShadowDistance);
				signature.Add(type?.DetailFadeDistance);
				signature.Add(entry?.Density);
				signature.Add(entry?.MinHeightScale);
				signature.Add(entry?.MaxHeightScale);
				signature.Add(entry?.MinWidthRatio);
				signature.Add(entry?.MaxWidthRatio);
				signature.Add(entry?.ClumpSizeMeters);
				signature.Add(entry?.ClumpStrength);
				signature.Add(entry?.MaxSlopeDegrees);
				signature.Add(entry?.MinHeight);
				signature.Add(entry?.MaxHeight);
				signature.Add(entry?.AlignToNormal);
			}
		}

		return signature.ToArray();
	}

	private sealed record Entry(TerrainDetailRuntime? Runtime, object?[] Signature);
}
