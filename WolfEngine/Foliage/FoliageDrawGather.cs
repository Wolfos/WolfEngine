using System.Numerics;
using System.Runtime.CompilerServices;
using WolfEngine.ECS;

namespace WolfEngine.Rendering;

/// <summary>
/// Hands terrains' placed foliage to the views' draw databases each sync. The GPU cull draws the instances; this only
/// keeps each renderer's mesh and material in the draw tables, and puts instances near the camera in the ray-tracing
/// scene. See docs/foliage.md.
/// </summary>
/// <remarks>
/// The ray-tracing instances are persistent records, chosen again only when the camera has moved
/// <see cref="RayTracingRefreshDistance"/> since they were last chosen, or the foliage or its terrain changed, so a still
/// camera costs nothing per instance. Each database is tracked separately, since snapshot databases take turns. Main
/// thread only.
/// </remarks>
internal sealed class FoliageDrawGather
{
	/// <summary>How far the camera moves, in metres, before the ray-tracing instances are chosen again.</summary>
	public const float RayTracingRefreshDistance = 4.0f;

	private readonly ConditionalWeakTable<GpuDrawDatabase, Dictionary<FoliageSetKey, RayTracingState>> _states = new();
	private readonly List<FoliageSetKey> _removeScratch = new();
	private readonly HashSet<int> _desiredScratch = new();

	private readonly record struct FoliageSetKey(int WorldId, Entity Entity);

	private sealed class RayTracingState
	{
		public TerrainFoliageRuntime? Runtime;
		public Matrix4x4 LocalToWorld;
		public Vector3 CameraPosition;
		public readonly HashSet<int> SubdrawIds = new();
		public int LastSyncStamp;
	}

	public void Gather(
		GpuDrawDatabase database,
		Entity entity,
		TerrainFoliageRuntime runtime,
		in Matrix4x4 localToWorld,
		Vector3 cameraPosition)
	{
		ArgumentNullException.ThrowIfNull(database);
		ArgumentNullException.ThrowIfNull(runtime);

		var key = new FoliageSetKey(database.CurrentWorldId, entity);
		var state = GetState(database, key);
		state.LastSyncStamp = database.SyncStamp;
		if (runtime.InstanceCount == 0)
		{
			RemoveRayTracingInstances(database, key, state);
			return;
		}

		database.TouchFoliage(entity, runtime, localToWorld);
		TouchResourceAnchors(database, entity, runtime);

		var unchanged = ReferenceEquals(state.Runtime, runtime) &&
		                state.LocalToWorld.Equals(localToWorld) &&
		                Vector3.Distance(state.CameraPosition, cameraPosition) < RayTracingRefreshDistance;
		if (unchanged && database.ReconcilesPersistentMeshes == false)
		{
			return;
		}

		if (unchanged == false)
		{
			state.Runtime = runtime;
			state.LocalToWorld = localToWorld;
			state.CameraPosition = cameraPosition;
		}

		UpdateRayTracingInstances(database, entity, state);
	}

	/// <summary>
	/// Removes the ray-tracing instances of every terrain this database's last sync did not gather, such as one that was
	/// deleted, disabled or lost its foliage. Call after the gather, before the database ends its sync.
	/// </summary>
	public void EndSync(GpuDrawDatabase database)
	{
		ArgumentNullException.ThrowIfNull(database);

		if (_states.TryGetValue(database, out var states) == false)
		{
			return;
		}

		_removeScratch.Clear();
		foreach (var (key, state) in states)
		{
			if (state.LastSyncStamp != database.SyncStamp)
			{
				_removeScratch.Add(key);
			}
		}

		foreach (var key in _removeScratch)
		{
			RemoveRayTracingInstances(database, key, states[key]);
			states.Remove(key);
		}
	}

	/// <summary>
	/// Sub-draw id of the record that keeps a type's renderer resident. Counts up from <see cref="int.MinValue"/>, so it
	/// never meets a terrain chunk's (positive) or a ray-tracing instance's (counting down from -1).
	/// </summary>
	internal static int GetAnchorSubdrawId(int typeIndex, int rendererIndex)
	{
		return int.MinValue + (typeIndex * FoliageTypeRuntime.MaxRendererCount) + rendererIndex;
	}

	/// <summary>Sub-draw id of a renderer of an instance in the ray-tracing scene.</summary>
	internal static int GetRayTracingSubdrawId(int instanceIndex, int rendererIndex)
	{
		return -1 - ((instanceIndex * FoliageTypeRuntime.MaxRendererCount) + rendererIndex);
	}

	private RayTracingState GetState(GpuDrawDatabase database, FoliageSetKey key)
	{
		var states = _states.GetOrCreateValue(database);
		if (states.TryGetValue(key, out var state) == false)
		{
			state = new RayTracingState();
			states.Add(key, state);
		}

		return state;
	}

	private static void TouchResourceAnchors(GpuDrawDatabase database, Entity entity, TerrainFoliageRuntime runtime)
	{
		for (var typeIndex = 0; typeIndex < runtime.Types.Length; typeIndex++)
		{
			if (runtime.Types[typeIndex] is not { } type || runtime.TypeInstanceCounts[typeIndex] == 0)
			{
				continue;
			}

			for (var rendererIndex = 0; rendererIndex < type.Renderers.Count; rendererIndex++)
			{
				var renderer = type.Renderers[rendererIndex];
				database.TouchResourceAnchor(
					entity,
					GetAnchorSubdrawId(typeIndex, rendererIndex),
					renderer.Mesh,
					renderer.Material);
			}
		}
	}

	/// <summary>
	/// Touches every level 0 renderer of each instance within its type's ray-tracing distance of the state's camera,
	/// measured like the cull does from the instance's bounds, and removes the ones that left it.
	/// </summary>
	private void UpdateRayTracingInstances(GpuDrawDatabase database, Entity entity, RayTracingState state)
	{
		_desiredScratch.Clear();
		var runtime = state.Runtime!;
		var localToWorld = state.LocalToWorld;
		var scale = FoliageTypeRuntime.GetMaxScale(localToWorld);
		if (runtime.MaxRayTracingDistance > 0.0f &&
		    scale > 0.0f &&
		    Matrix4x4.Invert(localToWorld, out var worldToLocal))
		{
			var localCamera = Vector3.Transform(state.CameraPosition, worldToLocal);
			for (var cellIndex = 0; cellIndex < runtime.SourceCells.Length; cellIndex++)
			{
				if (runtime.CellBounds[cellIndex].DistanceTo(localCamera) * scale > runtime.MaxRayTracingDistance)
				{
					continue;
				}

				TouchCell(database, entity, runtime, cellIndex, localCamera, scale, localToWorld);
			}
		}

		foreach (var subdrawId in state.SubdrawIds)
		{
			if (_desiredScratch.Contains(subdrawId) == false)
			{
				database.RemoveRayTracingInstance(database.CurrentWorldId, entity, subdrawId);
			}
		}

		state.SubdrawIds.Clear();
		state.SubdrawIds.UnionWith(_desiredScratch);
	}

	private void TouchCell(
		GpuDrawDatabase database,
		Entity entity,
		TerrainFoliageRuntime runtime,
		int cellIndex,
		Vector3 localCamera,
		float scale,
		in Matrix4x4 localToWorld)
	{
		var cell = runtime.SourceCells[cellIndex];
		var instanceStart = runtime.CellInstanceStarts[cellIndex];
		for (var i = 0; i < cell.InstanceCount; i++)
		{
			var instance = cell.GetInstance(i);
			if ((uint)instance.TypeIndex >= (uint)runtime.Types.Length ||
			    runtime.Types[instance.TypeIndex] is not { } type ||
			    type.RayTracingDistance <= 0.0f)
			{
				continue;
			}

			var bounds = TerrainFoliageRuntime.GetInstanceBounds(type, instance);
			var distance = (Vector3.Distance(bounds.Center, localCamera) - bounds.Radius) * scale;
			if (distance > type.RayTracingDistance)
			{
				continue;
			}

			var instanceToWorld = TerrainFoliageRuntime.GetInstanceTransform(instance) * localToWorld;
			for (var rendererIndex = 0; rendererIndex < type.Renderers.Count; rendererIndex++)
			{
				var renderer = type.Renderers[rendererIndex];
				if (renderer.FirstLevel != 0)
				{
					continue;
				}

				var subdrawId = GetRayTracingSubdrawId(instanceStart + i, rendererIndex);
				_desiredScratch.Add(subdrawId);
				database.TouchRayTracingInstance(
					entity,
					subdrawId,
					renderer.Mesh,
					renderer.Material,
					renderer.LocalTransform * instanceToWorld);
			}
		}
	}

	private static void RemoveRayTracingInstances(GpuDrawDatabase database, FoliageSetKey key, RayTracingState state)
	{
		foreach (var subdrawId in state.SubdrawIds)
		{
			database.RemoveRayTracingInstance(key.WorldId, key.Entity, subdrawId);
		}

		state.SubdrawIds.Clear();
		state.Runtime = null;
	}
}
