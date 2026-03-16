using System.Numerics;
using NUnit.Framework;
using WolfEngine.ECS;

namespace WolfEngine.ECS.Tests;

[TestFixture]
public class WorldTests
{
    [Test]
    public void SetEnabled_PersistsDisabledState()
    {
        var world = new World(WorldTag.All);
        var entity = world.CreateEntity("Entity", Matrix4x4.Identity);

        world.SetEnabled(entity, false);

        Assert.That(world.IsEnabled(entity), Is.False);
    }

    [Test]
    public void SetEnabled_PersistsEnabledState()
    {
        var world = new World(WorldTag.All);
        var entity = world.CreateEntity("Entity", Matrix4x4.Identity);
        world.SetEnabled(entity, false);

        world.SetEnabled(entity, true);

        Assert.That(world.IsEnabled(entity), Is.True);
    }

    [Test]
    public void ViewEntity_ReportsDisabledStateThroughWorld()
    {
        var world = new World(WorldTag.All);
        var entity = world.CreateEntity("Entity", Matrix4x4.Identity);
        world.SetEnabled(entity, false);

        var checkedEntry = false;
        foreach (var entry in world.View<LocalTransform, WorldTransform>())
        {
            if (entry.Entity != entity)
            {
                continue;
            }

            Assert.That(world.IsEnabled(entry.Entity), Is.False);
            checkedEntry = true;
        }

        Assert.That(checkedEntry, Is.True);
    }

    [Test]
    public void HasComponent_ReturnsFalseForStaleEntityGeneration()
    {
        var world = new World(WorldTag.All);
        var first = world.CreateEntity("First", Matrix4x4.Identity);
        world.DestroyEntity(first);

        var second = world.CreateEntity("Second", Matrix4x4.Identity);

        Assert.That(world.HasComponent<LocalTransform>(first), Is.False);
        Assert.That(world.HasComponent<LocalTransform>(second), Is.True);
    }

    [Test]
    public void ViewEntity_CanBeUsedWithGenerationSensitiveWorldApis()
    {
        var world = new World(WorldTag.All);
        var entity = world.CreateEntity("Entity", Matrix4x4.Identity);
        world.AddComponent(entity, new NameComponent { Name = "Test" });

        foreach (var entry in world.View<LocalTransform, WorldTransform>())
        {
            if (entry.Entity != entity)
            {
                continue;
            }

            var rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.2f, -0.1f, 0.0f));
            world.SetLocalRotation(entry.Entity, rotation);
            world.Translate(entry.Entity, new Vector3(1.0f, 2.0f, 3.0f), true);
            world.RemoveComponent<NameComponent>(entry.Entity);
            break;
        }

        ref var local = ref world.GetComponent<LocalTransform>(entity);
        Assert.That(local.LocalPosition, Is.EqualTo(new Vector3(1.0f, 2.0f, 3.0f)));
        Assert.That(local.LocalRotation, Is.EqualTo(Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.2f, -0.1f, 0.0f))));
        Assert.That(world.HasComponent<NameComponent>(entity), Is.False);
    }
}
