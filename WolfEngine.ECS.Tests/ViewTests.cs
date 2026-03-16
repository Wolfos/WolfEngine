using System.Numerics;
using NUnit.Framework;
using WolfEngine.ECS;

namespace WolfEngine.ECS.Tests;

[TestFixture]
public class ViewTests
{
    [Test]
    public void View_ReturnsEntityWithGeneration()
    {
        var world = new World(WorldTag.All);
        var entity = world.CreateEntity("Entity", Matrix4x4.Identity);

        var seen = false;
        foreach (var entry in world.View<LocalTransform, WorldTransform>())
        {
            Assert.That(entry.Entity, Is.EqualTo(entity));
            Assert.That(entry.Entity.Generation, Is.Not.EqualTo(0));
            seen = true;
        }

        Assert.That(seen, Is.True);
    }

    [Test]
    public void View_UsesReplacementGenerationAfterIndexReuse()
    {
        var world = new World(WorldTag.All);
        var first = world.CreateEntity("First", Matrix4x4.Identity);
        world.DestroyEntity(first);

        var second = world.CreateEntity("Second", Matrix4x4.Identity);

        Assert.That(second.Index, Is.EqualTo(first.Index));
        Assert.That(second.Generation, Is.GreaterThan(first.Generation));

        var sawOldGeneration = false;
        var sawReplacementGeneration = false;
        foreach (var entry in world.View<LocalTransform, WorldTransform>())
        {
            if (entry.Entity == first)
            {
                sawOldGeneration = true;
            }

            if (entry.Entity == second)
            {
                sawReplacementGeneration = true;
            }
        }

        Assert.That(sawOldGeneration, Is.False);
        Assert.That(sawReplacementGeneration, Is.True);
    }
}
