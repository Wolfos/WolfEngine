using System.Numerics;
using NUnit.Framework;
using WolfEngine.ECS;

namespace WolfEngine.ECS.Tests;

[TestFixture]
public class TransformSystemTests
{
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

        world.SetParent(vehicle, root);
        world.SetParent(graphics, vehicle);
        system.PreRender(0.0f, world);

        world.AddComponent<DirtyTransformRoot>(vehicle);
        world.ApplyPhysicsWorldPose(vehicle, new Vector3(20.0f, 0.0f, 0.0f), Quaternion.Identity);

        system.PreRender(0.0f, world);

        var vehicleWorld = world.GetComponent<WorldTransform>(vehicle);
        var graphicsWorld = world.GetComponent<WorldTransform>(graphics);
        Assert.That(world.HasComponent<DirtyTransformRoot>(vehicle), Is.False);
        Assert.That(vehicleWorld.LocalToWorld.Translation.X, Is.EqualTo(20.0f).Within(0.0001f));
        Assert.That(graphicsWorld.LocalToWorld.Translation.X, Is.EqualTo(22.0f).Within(0.0001f));
    }
}
