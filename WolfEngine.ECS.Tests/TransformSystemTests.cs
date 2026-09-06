using System.Numerics;
using NUnit.Framework;

namespace WolfEngine.ECS.Tests;

[TestFixture]
public class TransformSystemTests
{
    [Test]
    public void PreRender_MovingChild_UpdatesOnlyItsSubtreeUsingParentWorldMatrix()
    {
        var world = new World(WorldTag.All);
        var system = new TransformSystem();
        var parent = world.CreateEntity("Crates", new Vector3(10, 20, 30),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f), new Vector3(2));
        var child = CreateEntity(world, "Crate1", parent);
        var sibling = CreateEntity(world, "Crate2", parent);
        var descendant = CreateEntity(world, "Contents", child);
        var siblingDescendant = CreateEntity(world, "Other contents", sibling);
        system.PreRender(0, world);
        MarkTransformsConsumed(world);
        var siblingMatrix = world.GetComponent<WorldTransform>(sibling).LocalToWorld;

        world.SetLocalPosition(child, new Vector3(3, 4, 5));
        system.PreRender(0, world);

        var expected = world.GetComponent<LocalTransform>(child).GetTransform()
            * world.GetComponent<WorldTransform>(parent).LocalToWorld;
        Assert.That(world.GetComponent<WorldTransform>(child).LocalToWorld, Is.EqualTo(expected));
        Assert.That(world.GetComponent<WorldTransform>(descendant).LocalToWorld,
            Is.EqualTo(world.GetComponent<LocalTransform>(descendant).GetTransform() * expected));
        Assert.That(world.GetComponent<DirtyWorldTransform>(child).Consumed, Is.Zero);
        Assert.That(world.GetComponent<DirtyWorldTransform>(descendant).Consumed, Is.Zero);
        foreach (var unchanged in new[] { parent, sibling, siblingDescendant })
            Assert.That(world.GetComponent<DirtyWorldTransform>(unchanged).Consumed, Is.EqualTo(2));
        Assert.That(world.GetComponent<WorldTransform>(sibling).LocalToWorld, Is.EqualTo(siblingMatrix));
        AssertNoPendingTransforms(world);

        MarkTransformsConsumed(world);
        system.PreRender(0, world);
        foreach (var entry in world.View<DirtyWorldTransform>())
            Assert.That(entry.First.Consumed, Is.EqualTo(2));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PreRender_OverlappingDirtySubtrees_UpdatesAncestorsBeforeDescendants(bool parentFirst)
    {
        var world = new World(WorldTag.All);
        var system = new TransformSystem();
        var parent = CreateEntity(world, "Parent");
        var middle = CreateEntity(world, "Clean intermediate", parent);
        var child = CreateEntity(world, "Child", middle);
        var sibling = CreateEntity(world, "Sibling", parent);
        system.PreRender(0, world);

        if (parentFirst)
            world.SetLocalPosition(parent, new Vector3(10, 0, 0));
        world.SetLocalPosition(child, new Vector3(3, 0, 0));
        if (!parentFirst)
            world.SetLocalPosition(parent, new Vector3(10, 0, 0));
        system.PreRender(0, world);

        Assert.That(world.GetComponent<WorldTransform>(child).LocalToWorld.Translation.X, Is.EqualTo(14));
        Assert.That(world.GetComponent<WorldTransform>(sibling).LocalToWorld.Translation.X, Is.EqualTo(11));
        AssertNoPendingTransforms(world);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PreRender_MultipleIndependentDirtySubtrees_ProcessesEveryRoot(bool siblings)
    {
        var world = new World(WorldTag.All);
        var system = new TransformSystem();
        var parent = CreateEntity(world, "Parent");
        var moving = new Entity[3];
        for (var i = 0; i < moving.Length; i++)
            moving[i] = CreateEntity(world, "Moving", siblings ? parent : default);
        system.PreRender(0, world);
        MarkTransformsConsumed(world);

        for (var i = 0; i < moving.Length; i++)
            world.SetLocalPosition(moving[i], new Vector3(10 + i, 0, 0));
        system.PreRender(0, world);

        for (var i = 0; i < moving.Length; i++)
            Assert.That(world.GetComponent<WorldTransform>(moving[i]).LocalToWorld.Translation.X,
                Is.EqualTo(10 + i + (siblings ? 1 : 0)));
        Assert.That(world.GetComponent<DirtyWorldTransform>(parent).Consumed, Is.EqualTo(2));
        AssertNoPendingTransforms(world);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PreRender_HierarchyChangesBeforeFlush_UsesCurrentParent(bool detach)
    {
        var world = new World(WorldTag.All);
        var system = new TransformSystem();
        var oldParent = CreateEntity(world, "Old parent");
        var newParent = CreateEntity(world, "New parent");
        var child = CreateEntity(world, "Child", oldParent);
        var descendant = CreateEntity(world, "Descendant", child);
        system.PreRender(0, world);
        MarkTransformsConsumed(world);

        world.SetLocalPosition(child, new Vector3(3, 0, 0));
        if (detach)
            world.RemoveParent(child);
        else
        {
            world.SetParent(child, newParent);
            world.SetLocalPosition(newParent, new Vector3(20, 0, 0));
        }
        system.PreRender(0, world);

        Assert.That(world.GetComponent<WorldTransform>(child).LocalToWorld.Translation.X,
            Is.EqualTo(detach ? 3 : 23));
        Assert.That(world.GetComponent<WorldTransform>(descendant).LocalToWorld.Translation.X,
            Is.EqualTo(detach ? 4 : 24));
        Assert.That(world.GetComponent<DirtyWorldTransform>(oldParent).Consumed, Is.EqualTo(2));
        AssertNoPendingTransforms(world);
    }

    [Test]
    public void PreRender_AfterOnDemandParentUpdate_StillPropagatesToDescendants()
    {
        var world = new World(WorldTag.All);
        var system = new TransformSystem();
        var parent = CreateEntity(world, "Parent");
        var child = CreateEntity(world, "Child", parent);
        system.PreRender(0, world);

        world.SetLocalPosition(parent, new Vector3(10, 0, 0));
        Assert.That(world.TryGetWorldPoseAndScale(parent, out _, out _, out _), Is.True);
        Assert.That(world.GetComponent<LocalTransform>(parent).IsDirty, Is.False);
        system.PreRender(0, world);

        Assert.That(world.GetComponent<WorldTransform>(child).LocalToWorld.Translation.X, Is.EqualTo(11));
        AssertNoPendingTransforms(world);
    }

    private static Entity CreateEntity(World world, string name, Entity parent = default)
    {
        var entity = world.CreateEntity(name, Vector3.UnitX, Quaternion.Identity, Vector3.One);
        if (parent.IsValid)
            world.SetParent(entity, parent);
        return entity;
    }

    private static void MarkTransformsConsumed(World world)
    {
        foreach (var entry in world.View<DirtyWorldTransform>())
            entry.First.Consumed = 2;
    }

    private static void AssertNoPendingTransforms(World world)
    {
        Assert.That(world.GetComponentCount<DirtyTransformRoot>(), Is.Zero);
        foreach (var entry in world.View<LocalTransform>())
            Assert.That(entry.First.IsDirty, Is.False);
    }

    [Test]
    public void GetTag_ReturnsAll()
    {
        var system = new TransformSystem();

        var tag = system.GetTag();

        Assert.That(tag, Is.EqualTo(WorldTag.All));
    }

    [Test]
    public void PreRender_UpdatesWorldTransformsAndClearsDirtyRoot()
    {
        var world = new World(WorldTag.All);
        var system = new TransformSystem();
        var parent = world.CreateEntity("Parent", new Vector3(5.0f, 0.0f, 0.0f), Quaternion.Identity, Vector3.One);
        var child = world.CreateEntity("Child", new Vector3(2.0f, 0.0f, 0.0f), Quaternion.Identity, Vector3.One);

        system.PreRender(0.0f, world);
        world.SetParent(child, parent);

        system.PreRender(0.0f, world);

        var parentWorld = world.GetComponent<WorldTransform>(parent);
        var childWorld = world.GetComponent<WorldTransform>(child);
        Assert.That(parentWorld.LocalToWorld.Translation.X, Is.EqualTo(5.0f).Within(0.0001f));
        Assert.That(childWorld.LocalToWorld.Translation.X, Is.EqualTo(7.0f).Within(0.0001f));
        Assert.That(world.HasComponent<DirtyTransformRoot>(parent), Is.False);
        Assert.That(world.GetComponent<LocalTransform>(parent).IsDirty, Is.False);
        Assert.That(world.GetComponent<LocalTransform>(child).IsDirty, Is.False);
    }

	[Test]
	public void PreRender_ResetsExistingDirtyWorldTransformConsumption()
	{
		var world = new World(WorldTag.All);
		var system = new TransformSystem();
		var entity = world.CreateEntity("Moving", Vector3.Zero, Quaternion.Identity, Vector3.One);

		system.PreRender(0.0f, world);
		world.GetComponent<DirtyWorldTransform>(entity).Consumed = 2;
		world.SetLocalPosition(entity, Vector3.One);

		system.PreRender(0.0f, world);

		Assert.That(world.GetComponent<DirtyWorldTransform>(entity).Consumed, Is.Zero);
	}

    [Test]
    public void PreRender_AfterParentingDirtyTransforms_UsesParentTransformInsteadOfStaleChildRoot()
    {
        var world = new World(WorldTag.All);
        var system = new TransformSystem();

        var parentRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.5f);
        var parent = world.CreateEntity("Parent", Vector3.Zero, parentRotation, Vector3.One);
        var child = world.CreateEntity("Child", new Vector3(1.0f, 0.0f, 0.0f), Quaternion.Identity, Vector3.One);

        world.SetParent(child, parent);
        system.PreRender(0.0f, world);

        var childWorld = world.GetComponent<WorldTransform>(child);
        Assert.That(world.HasComponent<DirtyTransformRoot>(child), Is.False);
        Assert.That(childWorld.LocalToWorld.Translation.X, Is.EqualTo(0.0f).Within(0.0001f));
        Assert.That(childWorld.LocalToWorld.Translation.Z, Is.EqualTo(-1.0f).Within(0.0001f));
    }

    [Test]
    public void PreRender_AfterPhysicsPoseOnParentedEntity_IgnoresStalePhysicsDirtyRootForChildGraphics()
    {
        var world = new World(WorldTag.All);
        var system = new TransformSystem();

        var root = world.CreateEntity("Root", new Vector3(10.0f, 0.0f, 0.0f), Quaternion.Identity, Vector3.One);
        var vehicle = world.CreateEntity("Vehicle", Vector3.Zero, Quaternion.Identity, Vector3.One);
        var graphics = world.CreateEntity("Graphics", new Vector3(2.0f, 0.0f, 0.0f), Quaternion.Identity, Vector3.One);
        var sibling = CreateEntity(world, "Other vehicle", root);

        world.SetParent(vehicle, root);
        world.SetParent(graphics, vehicle);
        system.PreRender(0.0f, world);
        MarkTransformsConsumed(world);

        world.AddComponent<DirtyTransformRoot>(vehicle);
        world.ApplyPhysicsWorldPose(vehicle, new Vector3(20.0f, 0.0f, 0.0f), Quaternion.Identity);

        system.PreRender(0.0f, world);

        var vehicleWorld = world.GetComponent<WorldTransform>(vehicle);
        var graphicsWorld = world.GetComponent<WorldTransform>(graphics);
        Assert.That(world.HasComponent<DirtyTransformRoot>(vehicle), Is.False);
        Assert.That(vehicleWorld.LocalToWorld.Translation.X, Is.EqualTo(20.0f).Within(0.0001f));
        Assert.That(graphicsWorld.LocalToWorld.Translation.X, Is.EqualTo(22.0f).Within(0.0001f));
        Assert.That(world.GetComponent<DirtyWorldTransform>(root).Consumed, Is.EqualTo(2));
        Assert.That(world.GetComponent<DirtyWorldTransform>(sibling).Consumed, Is.EqualTo(2));
        Assert.That(world.GetComponent<DirtyWorldTransform>(graphics).Consumed, Is.Zero);
        AssertNoPendingTransforms(world);
    }
}
