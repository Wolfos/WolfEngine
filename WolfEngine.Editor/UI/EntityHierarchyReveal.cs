using System.Collections.Generic;
using WolfEngine.ECS;

namespace WolfEngine.Editor.UI;

/// <summary>
/// Works out which hierarchy nodes have to be unfolded for an entity to be on screen.
/// </summary>
public static class EntityHierarchyReveal
{
	/// <summary>
	/// Fills <paramref name="ancestors"/> with every parent above <paramref name="entity"/>, clearing
	/// it first. The entity itself is left out: revealing it means making its row visible, not
	/// unfolding its own children.
	/// </summary>
	public static void CollectAncestors(World world, Entity entity, HashSet<Entity> ancestors)
	{
		ArgumentNullException.ThrowIfNull(world);
		ArgumentNullException.ThrowIfNull(ancestors);

		ancestors.Clear();
		if (entity.IsValid == false || world.IsAlive(entity) == false)
		{
			return;
		}

		// A malformed parent chain would otherwise loop forever, and this runs against scene data that
		// reparenting and undo can leave in odd states.
		var visited = new HashSet<Entity> { entity };
		var current = entity;
		while (world.HasComponent<Parent>(current))
		{
			var parent = world.GetComponent<Parent>(current).Value;
			if (parent.IsValid == false || world.IsAlive(parent) == false || visited.Add(parent) == false)
			{
				return;
			}

			ancestors.Add(parent);
			current = parent;
		}
	}
}
