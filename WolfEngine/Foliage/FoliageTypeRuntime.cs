using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Mathematics;

namespace WolfEngine.Rendering;

/// <summary>One mesh renderer of a foliage type's prefab.</summary>
/// <param name="LocalTransform">The renderer's transform in the type's space: the prefab root at the origin.</param>
/// <param name="FirstLevel">The first level of the type's group this renderer draws in.</param>
/// <param name="LastLevel">The last level of the type's group this renderer draws in.</param>
internal readonly record struct FoliageRendererRuntime(
	Mesh Mesh,
	Material Material,
	Matrix4x4 LocalTransform,
	int FirstLevel,
	int LastLevel);

/// <summary>
/// A foliage type's prefab, flattened into what the cull and the ray-tracing scene draw: its renderers, and one level
/// of detail group over all of them. Immutable once built, so the render thread can read it while the next frame is
/// gathered. See docs/foliage.md.
/// </summary>
internal sealed class FoliageTypeRuntime
{
	/// <summary>Renderers past this are left out, with a warning.</summary>
	public const int MaxRendererCount = 8;

	private FoliageTypeRuntime(
		FoliageType type,
		Prefab prefab,
		FoliageRendererRuntime[] renderers,
		float[] screenSizes,
		BoundingSphere bounds)
	{
		Type = type;
		Prefab = prefab;
		Renderers = renderers;
		ScreenSizes = screenSizes;
		Bounds = bounds;
		CullDistance = MathF.Max(0.0f, type.CullDistance);
		ShadowDistance = Math.Clamp(type.ShadowDistance, 0.0f, CullDistance);
		RayTracingDistance = Math.Clamp(type.RayTracingDistance, 0.0f, CullDistance);
	}

	public FoliageType Type { get; }

	public Prefab Prefab { get; }

	public IReadOnlyList<FoliageRendererRuntime> Renderers { get; }

	/// <summary>The group's thresholds, one per transition, decreasing. Empty for a type drawn at level 0 only.</summary>
	public float[] ScreenSizes { get; }

	/// <summary>Bounds of the whole type in its own space, which levels are measured on.</summary>
	public BoundingSphere Bounds { get; }

	public float CullDistance { get; }

	public float ShadowDistance { get; }

	public float RayTracingDistance { get; }

	public int LevelCount => ScreenSizes.Length + 1;

	/// <summary>
	/// Instantiates the type's prefab into a private world to read its renderers and group, the way placing it in a
	/// scene would resolve them.
	/// </summary>
	/// <returns>Null when the prefab cannot be resolved or has no drawable renderer.</returns>
	public static FoliageTypeRuntime? TryCreate(FoliageType type, RenderGraph? renderGraph)
	{
		ArgumentNullException.ThrowIfNull(type);

		if (type.Prefab.IsValid == false || type.Prefab.Asset is not { } prefab)
		{
			return null;
		}

		var world = new World(WorldTag.Editor);
		var root = prefab.Instantiate(world, Vector3.Zero, Quaternion.Identity);
		new TransformSystem().PreRender(0.0f, world);

		var rootWorld = world.HasComponent<WorldTransform>(root)
			? world.GetComponent<WorldTransform>(root).LocalToWorld
			: Matrix4x4.Identity;

		var renderers = new List<FoliageRendererRuntime>();
		var meshBounds = new List<BoundingSphere>();
		foreach (var entry in world.View<WorldTransform, MeshRenderer>())
		{
			ref var renderer = ref entry.Second;
			if (renderer.TryValidate() == false)
			{
				continue;
			}

			if (renderers.Count == MaxRendererCount)
			{
				Console.WriteLine(
					$"Foliage: prefab '{prefab.AssetId}' has more than {MaxRendererCount} mesh renderers; the rest are not drawn.");
				break;
			}

			var firstLevel = 0;
			var lastLevel = GpuDrawLod.MaxLevel;
			if (world.HasComponent<LodGroupMember>(entry.Entity))
			{
				var member = world.GetComponent<LodGroupMember>(entry.Entity);
				firstLevel = Math.Clamp(member.FirstLevel, 0, GpuDrawLod.MaxLevel);
				lastLevel = member.LastLevel < 0
					? GpuDrawLod.MaxLevel
					: Math.Clamp(member.LastLevel, firstLevel, GpuDrawLod.MaxLevel);
			}

			renderGraph?.EnsureMaterialResources(renderer.Material);
			var localTransform = entry.First.LocalToWorld;
			renderers.Add(new FoliageRendererRuntime(renderer.Mesh, renderer.Material, localTransform, firstLevel, lastLevel));
			meshBounds.Add(TransformSphere(renderer.Mesh.BoundingSphere, localTransform));
		}

		if (renderers.Count == 0)
		{
			return null;
		}

		if (world.HasComponent<LodGroup>(root) &&
		    world.GetComponent<LodGroup>(root) is { IsValid: true } group)
		{
			var groupBounds = TransformSphere(new BoundingSphere(group.BoundsCenter, group.BoundsRadius), rootWorld);
			var screenSizes = group.ScreenSizes.Take(GpuDrawLod.MaxLevel).ToArray();

			return new FoliageTypeRuntime(type, prefab, renderers.ToArray(), screenSizes, groupBounds);
		}

		// Without a group, the first renderer's mesh levels decide for the whole type, measured on all of it.
		var firstMesh = renderers[0].Mesh;
		var meshScreenSizes = firstMesh.LodScreenSizes.Take(GpuDrawLod.MaxLevel).ToArray();

		return new FoliageTypeRuntime(type, prefab, renderers.ToArray(), meshScreenSizes, Enclose(meshBounds));
	}

	internal static BoundingSphere TransformSphere(in BoundingSphere sphere, in Matrix4x4 transform)
	{
		var center = Vector3.Transform(sphere.Center, transform);

		return new BoundingSphere(center, sphere.Radius * GetMaxScale(transform));
	}

	internal static float GetMaxScale(in Matrix4x4 transform)
	{
		return MathF.Max(
			new Vector3(transform.M11, transform.M12, transform.M13).Length(),
			MathF.Max(
				new Vector3(transform.M21, transform.M22, transform.M23).Length(),
				new Vector3(transform.M31, transform.M32, transform.M33).Length()));
	}

	private static BoundingSphere Enclose(IReadOnlyList<BoundingSphere> spheres)
	{
		var min = new Vector3(float.MaxValue);
		var max = new Vector3(float.MinValue);
		foreach (var sphere in spheres)
		{
			min = Vector3.Min(min, sphere.Center - new Vector3(sphere.Radius));
			max = Vector3.Max(max, sphere.Center + new Vector3(sphere.Radius));
		}

		var center = (min + max) * 0.5f;
		var radius = 0.0f;
		foreach (var sphere in spheres)
		{
			radius = MathF.Max(radius, Vector3.Distance(center, sphere.Center) + sphere.Radius);
		}

		return new BoundingSphere(center, radius);
	}
}
