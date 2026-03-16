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
}
