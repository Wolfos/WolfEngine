using WolfEngine.ECS;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.UI;

/// <summary>
/// Keeps <see cref="OutlineHighlight"/> in step with the entity selection.
///
/// This reconciles the whole set once per frame rather than hooking each of the
/// selection mutators, so deletions, undo, scene loads and play-mode transitions
/// need no special handling: whatever the selection is when the frame is drawn
/// is what gets outlined.
/// </summary>
public sealed class SelectionOutlineController
{
	/// <summary>Entities this controller added the component to, and may remove it from again.</summary>
	private readonly HashSet<Entity> _owned = new();

	private readonly HashSet<Entity> _desired = new();

	/// <summary>
	/// Outlines <paramref name="selection"/> and its descendants. Pass an empty
	/// selection to clear, which is what play mode does.
	/// </summary>
	public void Sync(World world, IReadOnlyList<Entity> selection)
	{
		ArgumentNullException.ThrowIfNull(world);
		ArgumentNullException.ThrowIfNull(selection);

		_desired.Clear();
		for (var i = 0; i < selection.Count; i++)
		{
			// The gizmo moves a selected entity's whole subtree, so the outline
			// covers the same set rather than just the clicked node.
			CollectSubtree(world, selection[i]);
		}

		foreach (var entity in _desired)
		{
			if (_owned.Add(entity) == false)
			{
				continue;
			}

			if (world.HasComponent<OutlineHighlight>(entity) == false)
			{
				world.AddComponent(entity, new OutlineHighlight
				{
					Color = OutlineHighlight.DefaultColor,
					ThicknessPixels = OutlineHighlight.DefaultThicknessPixels
				});
			}
			else
			{
				// Something else owns this outline -- a gameplay highlight, say.
				// Leave it alone, and do not claim the right to remove it later.
				_owned.Remove(entity);
			}
		}

		_owned.RemoveWhere(entity =>
		{
			if (_desired.Contains(entity))
			{
				return false;
			}

			if (world.IsAlive(entity) && world.HasComponent<OutlineHighlight>(entity))
			{
				world.RemoveComponent<OutlineHighlight>(entity);
			}

			return true;
		});
	}

	private void CollectSubtree(World world, Entity entity)
	{
		if (world.IsAlive(entity) == false || _desired.Add(entity) == false)
		{
			return;
		}

		if (world.HasComponent<Children>(entity) == false)
		{
			return;
		}

		var child = world.GetComponent<Children>(entity).First;
		while (child.IsValid)
		{
			var next = world.HasComponent<Sibling>(child)
				? world.GetComponent<Sibling>(child).Next
				: default;
			CollectSubtree(world, child);
			child = next;
		}
	}
}
