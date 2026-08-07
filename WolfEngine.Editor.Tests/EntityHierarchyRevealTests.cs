using System.Numerics;
using WolfEngine.Editor.UI;
using WolfEngine.ECS;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class EntityHierarchyRevealTests
{
	[TearDown]
	public void TearDown()
	{
		EditorGui.ClearEntitySelection();
	}

	[Test]
	public void CollectAncestors_ReturnsEveryParentButNotTheEntityItself()
	{
		var world = new World(WorldTag.Authoring);
		var root = world.CreateEntity("Root", Matrix4x4.Identity);
		var middle = world.CreateEntity("Middle", Matrix4x4.Identity);
		var leaf = world.CreateEntity("Leaf", Matrix4x4.Identity);
		world.SetParent(middle, root);
		world.SetParent(leaf, middle);

		var ancestors = new HashSet<Entity>();
		EntityHierarchyReveal.CollectAncestors(world, leaf, ancestors);

		Assert.That(ancestors, Is.EquivalentTo(new[] { root, middle }));
	}

	[Test]
	public void CollectAncestors_ReturnsNothingForARootEntity()
	{
		var world = new World(WorldTag.Authoring);
		var root = world.CreateEntity("Root", Matrix4x4.Identity);

		var ancestors = new HashSet<Entity> { root };
		EntityHierarchyReveal.CollectAncestors(world, root, ancestors);

		Assert.That(ancestors, Is.Empty);
	}

	[Test]
	public void CollectAncestors_ClearsStaleContentForADeadEntity()
	{
		var world = new World(WorldTag.Authoring);
		var root = world.CreateEntity("Root", Matrix4x4.Identity);
		var child = world.CreateEntity("Child", Matrix4x4.Identity);
		world.SetParent(child, root);
		world.DestroyEntity(child);

		var ancestors = new HashSet<Entity> { root };
		EntityHierarchyReveal.CollectAncestors(world, child, ancestors);

		Assert.That(ancestors, Is.Empty);
	}

	[Test]
	public void SelectingAnEntityRaisesASingleRevealRequest()
	{
		var world = new World(WorldTag.Authoring);
		var entity = world.CreateEntity("Entity", Matrix4x4.Identity);

		EditorGui.ReplaceEntitySelection(entity, world, requestFocus: false);

		Assert.That(EditorGui.ConsumeSelectionRevealRequest(out var revealed), Is.True);
		Assert.That(revealed, Is.EqualTo(entity));
		Assert.That(
			EditorGui.ConsumeSelectionRevealRequest(out _),
			Is.False,
			"A reveal must not repeat while the selection merely persists, or the branch could never be collapsed.");
	}

	[Test]
	public void AddingToASelectionRevealsTheEntityThatWasJustAdded()
	{
		var world = new World(WorldTag.Authoring);
		var first = world.CreateEntity("First", Matrix4x4.Identity);
		var second = world.CreateEntity("Second", Matrix4x4.Identity);

		EditorGui.ReplaceEntitySelection(first, world, requestFocus: false);
		EditorGui.AddEntitySelection(second, world, requestFocus: false);

		Assert.That(EditorGui.ConsumeSelectionRevealRequest(out var revealed), Is.True);
		Assert.That(revealed, Is.EqualTo(second));
	}

	[Test]
	public void DiscardingARequestSuppressesTheReveal()
	{
		var world = new World(WorldTag.Authoring);
		var entity = world.CreateEntity("Entity", Matrix4x4.Identity);

		EditorGui.ReplaceEntitySelection(entity, world, requestFocus: false);
		EditorGui.DiscardSelectionRevealRequest();

		Assert.That(EditorGui.ConsumeSelectionRevealRequest(out _), Is.False);
	}

	[Test]
	public void ClearingTheSelectionLeavesNothingToReveal()
	{
		var world = new World(WorldTag.Authoring);
		var entity = world.CreateEntity("Entity", Matrix4x4.Identity);

		EditorGui.ReplaceEntitySelection(entity, world, requestFocus: false);
		EditorGui.ClearEntitySelection();

		Assert.That(EditorGui.ConsumeSelectionRevealRequest(out _), Is.False);
	}

	[Test]
	public void TogglingTheLastEntityOutOfASelectionLeavesNothingToReveal()
	{
		var world = new World(WorldTag.Authoring);
		var entity = world.CreateEntity("Entity", Matrix4x4.Identity);

		EditorGui.ReplaceEntitySelection(entity, world, requestFocus: false);
		EditorGui.ConsumeSelectionRevealRequest(out _);
		EditorGui.ToggleEntitySelection(entity, world, requestFocus: false);

		Assert.That(EditorGui.HasSelectedEntity, Is.False);
		Assert.That(EditorGui.ConsumeSelectionRevealRequest(out _), Is.False);
	}
}
