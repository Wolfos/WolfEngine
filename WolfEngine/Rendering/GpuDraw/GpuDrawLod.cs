using System.Numerics;

namespace WolfEngine.Rendering;

/// <summary>
/// How a drawn instance takes part in a <see cref="LodGroup"/>: the group decides one level for all of its renderers,
/// measured on the group's bounds, and each renderer draws only within its own range of group levels. The default
/// value is an instance outside any group, which picks a level from its own mesh's thresholds and bounds.
/// See docs/mesh-lod.md.
/// </summary>
/// <param name="WorldSphere">The group's bounds in world space: centre in xyz, radius in w.</param>
/// <param name="ScreenSizes">
/// The group's thresholds, one per transition, decreasing. Compared by reference, so a group keeps one array until its
/// thresholds change.
/// </param>
/// <param name="FirstLevel">The first group level this renderer draws in.</param>
/// <param name="LastLevel">The last group level this renderer draws in.</param>
public readonly record struct GpuDrawLod(
	Vector4 WorldSphere,
	float[]? ScreenSizes,
	int FirstLevel,
	int LastLevel)
{
	/// <summary>Highest group level a renderer's range can name.</summary>
	public const int MaxLevel = GpuDrawBatchLodData.MaxLevelCount - 1;

	public bool IsGrouped => ScreenSizes is not null;
}
