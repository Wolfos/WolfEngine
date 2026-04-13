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

	public void CollectVisibleTerrain(
		RenderGraph renderGraph,
		World world,
		Entity entity,
		in TerrainComponent component,
		in WorldTransform transform,
		Vector3 cameraOrigin,
		Matrix4x4 viewProjection,
		List<TerrainSnapshotRecord> destination)
	{
		ArgumentNullException.ThrowIfNull(renderGraph);
		ArgumentNullException.ThrowIfNull(world);
		ArgumentNullException.ThrowIfNull(destination);
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

		runtime.CollectVisibleRecords(renderGraph, cameraOrigin, viewProjection, transform.LocalToWorld, destination);
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
