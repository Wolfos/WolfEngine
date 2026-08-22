using System.Runtime.CompilerServices;
using WolfEngine.ECS;

namespace WolfEngine;

internal static class TerrainRuntimeRegistry
{
	private static readonly Lock SyncRoot = new();
	private static readonly Dictionary<TerrainRuntimeKey, TerrainRuntimeData> RuntimeByKey = new();

	public static TerrainRuntimeData GetOrCreateRuntime(World world, Entity entity)
	{
		ArgumentNullException.ThrowIfNull(world);
		var key = new TerrainRuntimeKey(world, entity);
		lock (SyncRoot)
		{
			if (RuntimeByKey.TryGetValue(key, out var runtime))
			{
				return runtime;
			}

			runtime = new TerrainRuntimeData();
			RuntimeByKey.Add(key, runtime);
			return runtime;
		}
	}

	public static void MarkHeightmapEdited(World world, Entity entity, in TerrainHeightmapDirtyRegion dirtyRegion)
	{
		ArgumentNullException.ThrowIfNull(world);
		if (dirtyRegion.IsEmpty)
		{
			return;
		}

		lock (SyncRoot)
		{
			var key = new TerrainRuntimeKey(world, entity);
			if (RuntimeByKey.TryGetValue(key, out var runtime) == false)
			{
				runtime = new TerrainRuntimeData();
				RuntimeByKey.Add(key, runtime);
			}

			runtime.MarkHeightmapEdited(dirtyRegion);
		}
	}

	public static void RemoveWorld(World world)
	{
		ArgumentNullException.ThrowIfNull(world);
		lock (SyncRoot)
		{
			var keysToRemove = new List<TerrainRuntimeKey>();
			foreach (var key in RuntimeByKey.Keys)
			{
				if (ReferenceEquals(key.World, world))
				{
					keysToRemove.Add(key);
				}
			}

			for (var i = 0; i < keysToRemove.Count; i++)
			{
				RuntimeByKey.Remove(keysToRemove[i]);
			}
		}
	}

	private readonly record struct TerrainRuntimeKey(World World, Entity Entity)
	{
		public override int GetHashCode()
		{
			return HashCode.Combine(RuntimeHelpers.GetHashCode(World), Entity);
		}
	}
}
