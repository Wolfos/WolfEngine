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

/// <summary>
/// What the foliage cull draws from: a terrain's placed foliage, or its detail foliage generated for the frame. The cull
/// runs over <see cref="ChunkCount"/> chunks of packed instances, at most <see cref="SlotCount"/> of them, naming types by
/// index into <see cref="Types"/>. See docs/foliage.md.
/// </summary>
internal interface IFoliageCullSource
{
	/// <summary>Null for a type that could not be resolved.</summary>
	IReadOnlyList<FoliageTypeRuntime?> Types { get; }

	/// <summary>Instance slots across every chunk, which per-instance state such as the level is kept for.</summary>
	int SlotCount { get; }

	int ChunkCount { get; }

	/// <summary>Every slot's renderers added up: what drawing all of them at once would expand into.</summary>
	int RendererInstanceCount { get; }

	/// <summary>How many instances of a type can be drawn at once, which is how many batch slots it reserves.</summary>
	int GetTypeSlotCount(int typeIndex);
}

/// <summary>One terrain's placed foliage, as touched by a view's gather. See docs/foliage.md.</summary>
/// <param name="LocalToWorld">The terrain entity's transform, which instance positions are relative to.</param>
internal readonly record struct GpuFoliageSet(
	int WorldId,
	Entity Entity,
	TerrainFoliageRuntime Runtime,
	Matrix4x4 LocalToWorld);

/// <summary>
/// One terrain's detail foliage, as touched by a view's gather. The GPU generates its plants from the terrain's layers
/// around <see cref="LocalCamera"/> every frame. See docs/foliage.md.
/// </summary>
/// <param name="LocalToWorld">The terrain entity's transform, which generated positions are relative to.</param>
/// <param name="TerrainMaterial">The material the terrain draws with, whose terrain table entry holds its maps.</param>
/// <param name="TerrainSize">The terrain's size along its local X and Z.</param>
/// <param name="LocalCamera">The view's camera in the terrain's local space.</param>
internal readonly record struct GpuDetailFoliageSet(
	int WorldId,
	Entity Entity,
	TerrainDetailRuntime Runtime,
	Matrix4x4 LocalToWorld,
	Material TerrainMaterial,
	Vector2 TerrainSize,
	float HeightScale,
	Vector3 LocalCamera);
