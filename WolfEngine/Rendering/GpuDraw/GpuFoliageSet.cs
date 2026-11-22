using System.Numerics;
using WolfEngine.ECS;

namespace WolfEngine.Rendering;

/// <summary>What a draw record contributes to the frame.</summary>
public enum GpuDrawRole
{
	/// <summary>Rasterised by the cull, and in the ray-tracing scene when its material allows.</summary>
	Draw = 0,

	/// <summary>In the ray-tracing scene only. The raster cull skips it.</summary>
	RayTracingOnly = 1,

	/// <summary>
	/// Neither rasterised nor ray traced. It only keeps its mesh and material in the draw tables, for instances the GPU
	/// writes itself.
	/// </summary>
	ResourcesOnly = 2
}

/// <summary>One terrain's placed foliage, as touched by a view's gather. See docs/foliage.md.</summary>
/// <param name="LocalToWorld">The terrain entity's transform, which instance positions are relative to.</param>
internal readonly record struct GpuFoliageSet(
	int WorldId,
	Entity Entity,
	TerrainFoliageRuntime Runtime,
	Matrix4x4 LocalToWorld);
