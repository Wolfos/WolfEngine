using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using WolfEngine.ECS;
using WolfEngine.Rendering;

namespace WolfEngine;

internal sealed class TerrainRuntimeCache
{
	private readonly Dictionary<TerrainRuntimeKey, TerrainRuntimeData> _runtimeByKey = new();

	public void CollectSharedTerrain(
		RenderGraph renderGraph,
		World world,
		Entity entity,
		ref TerrainComponent component,
		in WorldTransform transform,
		Vector3 cameraOrigin,
		GpuDrawDatabase gpuDrawDatabase)
	{
		ArgumentNullException.ThrowIfNull(renderGraph);
		ArgumentNullException.ThrowIfNull(world);
		ArgumentNullException.ThrowIfNull(gpuDrawDatabase);
		var key = new TerrainRuntimeKey(world, entity);
		if (_runtimeByKey.TryGetValue(key, out var runtime) == false)
		{
			runtime = new TerrainRuntimeData();
			_runtimeByKey.Add(key, runtime);
		}

		if (runtime.EnsureBuilt(component) == false)
		{
			return;
		}

		var material = ResolveTerrainMaterial(ref component, renderGraph);
		var records = new List<TerrainChunkDrawRecord>(runtime.Chunks.Count);
		runtime.CollectChunkDrawRecords(renderGraph, material, cameraOrigin, transform.LocalToWorld, records);
		for (var i = 0; i < records.Count; i++)
		{
			var record = records[i];
			gpuDrawDatabase.TouchTerrainChunk(entity, record.ChunkIndex, record.Mesh, record.Material, record.Surface, record.WorldTransform);
		}
	}

	private static Material ResolveTerrainMaterial(ref TerrainComponent component, RenderGraph renderGraph)
	{
		ArgumentNullException.ThrowIfNull(renderGraph);

		component.Material ??= CreateTerrainMaterial();
		renderGraph.EnsureMaterialResources(component.Material);
		return component.Material;
	}

	private static Material CreateTerrainMaterial()
	{
		return new Material("__terrain__")
		{
			Color = ColorRGBA.White,
			AlphaMode = AlphaMode.Opaque,
			AlphaCutoff = 0.0f,
			MetallicFactor = 0.0f,
			RoughnessFactor = 1.0f,
			EmissiveFactor = Vector3.Zero,
			EmissiveIntensity = 0.0f
		};
	}

	private readonly struct TerrainRuntimeKey : IEquatable<TerrainRuntimeKey>
	{
		public TerrainRuntimeKey(World world, Entity entity)
		{
			World = world;
			Entity = entity;
		}

		private World World { get; }
		private Entity Entity { get; }

		public bool Equals(TerrainRuntimeKey other) => ReferenceEquals(World, other.World) && Entity.Equals(other.Entity);

		public override bool Equals(object? obj) => obj is TerrainRuntimeKey other && Equals(other);

		public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(World), Entity);
	}
}
