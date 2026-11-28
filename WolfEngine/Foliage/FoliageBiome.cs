using WolfEngine.AssetPipeline;

namespace WolfEngine;

/// <summary>
/// The detail foliage that grows on a terrain layer: grass, flowers and small plants generated on the GPU near the
/// camera instead of stored per plant. Assigned per layer with <see cref="TerrainLayerDefinition.Biome"/>; where layers
/// blend, each entry's density blends by layer weight. See docs/foliage.md.
/// </summary>
[RuntimeAsset(AssetType.DataAsset, typeof(FoliageBiome), typeof(IDataAssetRuntimeResolver))]
public sealed class FoliageBiome : IDataAsset
{
	public List<FoliageBiomeEntry> Entries { get; set; } = [];
}

/// <summary>One kind of detail foliage in a biome, and how it grows.</summary>
public sealed class FoliageBiomeEntry
{
	/// <summary>What grows. Its cull distance is how far from the camera detail of this type is generated.</summary>
	public AssetRef<FoliageType> Type { get; set; }

	/// <summary>Plants per square metre where the layer is fully painted.</summary>
	public float Density { get; set; } = 1.0f;

	/// <summary>Smallest scale along the type's up axis.</summary>
	public float MinHeightScale { get; set; } = 0.8f;

	/// <summary>Largest scale along the type's up axis.</summary>
	public float MaxHeightScale { get; set; } = 1.2f;

	/// <summary>
	/// Smallest scale along the type's X and Z axes, as a fraction of the height scale picked for the plant.
	/// </summary>
	public float MinWidthRatio { get; set; } = 1.0f;

	/// <summary>Largest scale along the type's X and Z axes, as a fraction of the height scale picked for the plant.</summary>
	public float MaxWidthRatio { get; set; } = 1.0f;

	/// <summary>
	/// Size, in metres, of the patches that clumping noise raises and lowers density across. Zero turns clumping off.
	/// </summary>
	public float ClumpSizeMeters { get; set; }

	/// <summary>How strongly clumping noise changes density: 0 leaves it even, 1 empties the gaps between patches.</summary>
	public float ClumpStrength { get; set; }

	/// <summary>Steepest terrain, in degrees, the entry grows on.</summary>
	public float MaxSlopeDegrees { get; set; } = 90.0f;

	/// <summary>Lowest terrain height, in the terrain's local metres, the entry grows at.</summary>
	public float MinHeight { get; set; } = float.MinValue;

	/// <summary>Highest terrain height, in the terrain's local metres, the entry grows at.</summary>
	public float MaxHeight { get; set; } = float.MaxValue;

	/// <summary>How far a plant leans with the terrain: 0 grows straight up, 1 along the surface normal.</summary>
	public float AlignToNormal { get; set; }
}
