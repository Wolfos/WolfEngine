using WolfEngine.Rendering;

namespace WolfEngine;

/// <summary>
/// Where a foliage material takes its shading normal from. Mirrors the GPU_MATERIAL_FOLIAGE_NORMAL_ constants in
/// draw_data.slang.
/// </summary>
public enum FoliageNormalMode
{
	/// <summary>The mesh normal, with the normal map applied, as authored.</summary>
	Mesh = 0,

	/// <summary>
	/// Normals bent away from the centre of the instance's bounds, so a crown of cards shades as one volume.
	/// </summary>
	Spherical = 1,

	/// <summary>World up, for grass, so blades shade like the ground they grow from.</summary>
	Up = 2
}

/// <summary>
/// The foliage shading model of a material: alpha-tested, two-sided cards that let light through from behind.
/// A material without it shades as the standard surface model.
/// </summary>
public readonly record struct FoliageShading
{
	public FoliageShading(
		float translucency,
		FoliageNormalMode normalMode,
		float normalBlend,
		float tintVariation,
		ColorRGBA healthyTint,
		ColorRGBA dryTint)
	{
		Translucency = translucency;
		NormalMode = normalMode;
		NormalBlend = normalBlend;
		TintVariation = tintVariation;
		HealthyTint = healthyTint;
		DryTint = dryTint;
	}

	/// <summary>How much of the light reaching the back of a leaf comes through it, tinted by albedo, 0 to 1.</summary>
	public float Translucency { get; }

	public FoliageNormalMode NormalMode { get; }

	/// <summary>How far the shading normal is pulled from the mesh normal toward the normal mode's, 0 to 1.</summary>
	public float NormalBlend { get; }

	/// <summary>
	/// How strongly each instance is tinted by a colour between <see cref="HealthyTint"/> and <see cref="DryTint"/>,
	/// picked by a hash of its world position. Zero turns the tint off.
	/// </summary>
	public float TintVariation { get; }

	public ColorRGBA HealthyTint { get; }

	public ColorRGBA DryTint { get; }
}
