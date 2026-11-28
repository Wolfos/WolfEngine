using WolfEngine.AssetPipeline;

namespace WolfEngine;

/// <summary>
/// What a piece of foliage is, independent of where it grows: the prefab it draws and how far it is drawn, shadowed and
/// ray traced. Where and how densely it grows belongs to the placement that uses it. See docs/foliage.md.
/// </summary>
[RuntimeAsset(AssetType.DataAsset, typeof(FoliageType), typeof(IDataAssetRuntimeResolver))]
public sealed class FoliageType : IDataAsset
{
	/// <summary>
	/// The prefab drawn for every instance, usually a model with a <see cref="LodGroup"/> on its root. Only its mesh
	/// renderers, their transforms relative to the root, the root's group and each renderer's
	/// <see cref="LodGroupMember"/> are used.
	/// </summary>
	public AssetRef<Prefab> Prefab { get; set; }

	/// <summary>Distance from the camera, in metres, beyond which an instance is not drawn.</summary>
	public float CullDistance { get; set; } = 500.0f;

	/// <summary>Distance from the camera, in metres, beyond which an instance casts no shadow. Zero casts none.</summary>
	public float ShadowDistance { get; set; } = 150.0f;

	/// <summary>
	/// Distance from the camera, in metres, within which instances are in the ray-tracing scene, at level 0. Zero keeps
	/// the type out of it.
	/// </summary>
	public float RayTracingDistance { get; set; } = 100.0f;

	/// <summary>
	/// Grown as detail foliage, the distance in metres before <see cref="CullDistance"/> over which plants thin out and
	/// shrink away, so the edge of the generated area does not show. Detail is never ray traced.
	/// </summary>
	public float DetailFadeDistance { get; set; } = 15.0f;
}
