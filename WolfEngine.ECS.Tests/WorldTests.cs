using System.Numerics;
using NUnit.Framework;

namespace WolfEngine.ECS.Tests;

[TestFixture]
public class WorldTests
{
    [Test]
    public void CreateEntity_ReturnsValidEntity()
    {
        var world = new World(WorldTag.All);

        var entity = world.CreateEntity();

        Assert.That(entity.IsValid, Is.True);
        Assert.That(world.HasComponent<NameComponent>(entity), Is.False);
    }

    [Test]
    public void CreateEntity_WithName_AddsNameComponent()
    {
        var world = new World(WorldTag.All);

        var entity = world.CreateEntity("Entity");

        Assert.That(world.GetComponent<NameComponent>(entity).Name, Is.EqualTo("Entity"));
    }

    [Test]
    public void CreateEntity_WithTransformParameters_AddsConfiguredTransform()
    {
        var world = new World(WorldTag.All);
        var position = new Vector3(1.0f, 2.0f, 3.0f);
        var rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.2f, -0.3f, 0.4f));
        var scale = new Vector3(4.0f, 5.0f, 6.0f);

        var entity = world.CreateEntity("Entity", position, rotation, scale);

        ref var local = ref world.GetComponent<LocalTransform>(entity);
        AssertVector3(local.LocalPosition, position);
        AssertQuaternion(local.LocalRotation, rotation);
        AssertVector3(local.LocalScale, scale);
        Assert.That(world.HasComponent<WorldTransform>(entity), Is.True);
    }

    [Test]
    public void CreateEntity_WithMatrix_AddsConfiguredTransform()
    {
        var world = new World(WorldTag.All);
        var position = new Vector3(7.0f, 8.0f, 9.0f);
        var rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.1f, 0.2f, 0.3f));
        var scale = new Vector3(2.0f, 3.0f, 4.0f);
        var matrix =
            Matrix4x4.CreateScale(scale) *
            Matrix4x4.CreateFromQuaternion(rotation) *
            Matrix4x4.CreateTranslation(position);

        var entity = world.CreateEntity("Entity", matrix);

        ref var local = ref world.GetComponent<LocalTransform>(entity);
        AssertVector3(local.LocalPosition, position);
        AssertQuaternion(local.LocalRotation, rotation);
        AssertVector3(local.LocalScale, scale);
    }

    [Test]
    public void DestroyEntity_RemovesEntityFromGetAllEntities()
    {
        var world = new World(WorldTag.All);
        var entity = world.CreateEntity();
        var entities = new List<Entity>();

        world.DestroyEntity(entity);
        world.GetAllEntities(entities);

        Assert.That(entities, Does.Not.Contain(entity));
    }

    [Test]
    public void DestroyEntity_RemovesComponentsFromDestroyedEntity()
    {
        var world = new World(WorldTag.All);
        var entity = world.CreateEntity("Entity");
        world.AddComponent(entity, new TestComponentA { Value = 7 });

        world.DestroyEntity(entity);

        Assert.That(world.HasComponent<NameComponent>(entity), Is.False);
        Assert.That(world.HasComponent<TestComponentA>(entity), Is.False);
    }

    [Test]
    public void DestroyEntity_DestroyingParentAlsoDestroysDescendants()
    {
        var world = new World(WorldTag.All);
        var parent = CreateTransformEntity(world, "Parent", Vector3.Zero);
        var child = CreateTransformEntity(world, "Child", Vector3.One);
        var grandchild = CreateTransformEntity(world, "Grandchild", Vector3.One * 2.0f);
        world.SetParent(child, parent);
        world.SetParent(grandchild, child);

        world.DestroyEntity(parent);

        Assert.That(world.IsAlive(parent), Is.False);
        Assert.That(world.IsAlive(child), Is.False);
        Assert.That(world.IsAlive(grandchild), Is.False);
        Assert.That(world.HasComponent<Parent>(child), Is.False);
        Assert.That(world.HasComponent<Parent>(grandchild), Is.False);
    }

    [Test]
    public void DestroyEntity_DestroyingMiddleSiblingRepairsSiblingChain()
    {
        var world = new World(WorldTag.All);
        var parent = CreateTransformEntity(world, "Parent", Vector3.Zero);
        var first = CreateTransformEntity(world, "First", Vector3.One);
        var middle = CreateTransformEntity(world, "Middle", Vector3.One * 2.0f);
        var last = CreateTransformEntity(world, "Last", Vector3.One * 3.0f);
        world.SetParent(first, parent);
        world.SetParent(middle, parent);
        world.SetParent(last, parent);

        world.DestroyEntity(middle);

        Assert.That(world.IsAlive(middle), Is.False);
        Assert.That(world.GetComponent<Children>(parent).First, Is.EqualTo(first));
        Assert.That(world.GetComponent<Sibling>(first).Next, Is.EqualTo(last));
        Assert.That(world.HasComponent<Parent>(last), Is.True);
    }

    [Test]
    public void DestroyEntity_DestroyingOnlyChildRemovesParentChildrenComponent()
    {
        var world = new World(WorldTag.All);
        var parent = CreateTransformEntity(world, "Parent", Vector3.Zero);
        var child = CreateTransformEntity(world, "Child", Vector3.One);
        world.SetParent(child, parent);

        world.DestroyEntity(child);

        Assert.That(world.IsAlive(child), Is.False);
        Assert.That(world.HasComponent<Children>(parent), Is.False);
    }

    [Test]
    public void DestroyEntity_DestroyingFirstChildPromotesNextSiblingToParentChildrenComponent()
    {
        var world = new World(WorldTag.All);
        var parent = CreateTransformEntity(world, "Parent", Vector3.Zero);
        var first = CreateTransformEntity(world, "First", Vector3.One);
        var second = CreateTransformEntity(world, "Second", Vector3.One * 2.0f);
        world.SetParent(first, parent);
        world.SetParent(second, parent);

        world.DestroyEntity(first);

        Assert.That(world.IsAlive(first), Is.False);
        Assert.That(world.GetComponent<Children>(parent).First, Is.EqualTo(second));
        Assert.That(world.HasComponent<Parent>(second), Is.True);
    }

    [Test]
    public void AddComponent_AddsComponentToEntity()
    {
        var world = new World(WorldTag.All);
        var entity = world.CreateEntity();

        world.AddComponent(entity, new TestComponentA { Value = 5 });

        Assert.That(world.HasComponent<TestComponentA>(entity), Is.True);
    }

    [Test]
    public void GetComponent_ReturnsStoredComponent()
    {
        var world = new World(WorldTag.All);
        var entity = world.CreateEntity();
        world.AddComponent(entity, new TestComponentA { Value = 8 });

        var component = world.GetComponent<TestComponentA>(entity);

        Assert.That(component.Value, Is.EqualTo(8));
    }

    [Test]
    public void HasComponent_ReturnsTrueWhenComponentExists()
    {
        var world = new World(WorldTag.All);
        var entity = world.CreateEntity();
        world.AddComponent(entity, new TestComponentA { Value = 1 });

        var hasComponent = world.HasComponent<TestComponentA>(entity);

        Assert.That(hasComponent, Is.True);
    }

    [Test]
    public void RemoveComponent_RemovesStoredComponent()
    {
        var world = new World(WorldTag.All);
        var entity = world.CreateEntity();
        world.AddComponent(entity, new TestComponentA { Value = 1 });

        world.RemoveComponent<TestComponentA>(entity);

        Assert.That(world.HasComponent<TestComponentA>(entity), Is.False);
    }

    [Test]
    public void RemoveComponent_WithRuntimeType_RemovesStoredComponent()
    {
        var world = new World(WorldTag.All);
        var entity = world.CreateEntity();
        world.AddComponent(entity, new TestComponentA { Value = 1 });

        world.RemoveComponent(entity, typeof(TestComponentA));

        Assert.That(world.HasComponent<TestComponentA>(entity), Is.False);
    }

    [Test]
    public void RemoveComponentPool_WithRuntimeType_RemovesPool()
    {
        var world = new World(WorldTag.All);
        var entity = world.CreateEntity();
        world.AddComponent(entity, new TestComponentA { Value = 1 });

        world.RemoveComponentPool(typeof(TestComponentA));

        Assert.That(world.HasComponent<TestComponentA>(entity), Is.False);
    }

    [Test]
    public void View_WithSingleComponent_ReturnsMatchingEntities()
    {
        var world = new World(WorldTag.All);
        var included = world.CreateEntity();
        var excluded = world.CreateEntity();
        world.AddComponent(included, new TestComponentA { Value = 1 });

        var entities = new List<Entity>();
        foreach (var entry in world.View<TestComponentA>())
        {
            entities.Add(entry.Entity);
        }

        Assert.That(entities, Does.Contain(included));
        Assert.That(entities, Does.Not.Contain(excluded));
    }

	[Test]
	public void SetEnabled_MarksWorldTransformChanged()
	{
		var world = new World(WorldTag.All);
		var entity = world.CreateEntity("Toggle", Vector3.Zero, Quaternion.Identity, Vector3.One);
		world.RemoveComponent<DirtyWorldTransform>(entity);

		world.SetEnabled(entity, false);

		Assert.That(world.HasComponent<DirtyWorldTransform>(entity), Is.True);
	}

    [Test]
    public void View_WithTwoComponents_ReturnsIntersection()
    {
        var world = new World(WorldTag.All);
        var included = world.CreateEntity();
        var excluded = world.CreateEntity();
        world.AddComponent(included, new TestComponentA { Value = 1 });
        world.AddComponent(included, new TestComponentB { Value = 2 });
        world.AddComponent(excluded, new TestComponentA { Value = 3 });

        var entities = new List<Entity>();
        foreach (var entry in world.View<TestComponentA, TestComponentB>())
        {
            entities.Add(entry.Entity);
        }

        Assert.That(entities, Is.EquivalentTo(new[] { included }));
    }

    [Test]
    public void View_WithThreeComponents_ReturnsIntersection()
    {
        var world = new World(WorldTag.All);
        var included = world.CreateEntity();
        var excluded = world.CreateEntity();
        world.AddComponent(included, new TestComponentA { Value = 1 });
        world.AddComponent(included, new TestComponentB { Value = 2 });
        world.AddComponent(included, new TestComponentC { Value = 3 });
        world.AddComponent(excluded, new TestComponentA { Value = 4 });
        world.AddComponent(excluded, new TestComponentB { Value = 5 });

        var entities = new List<Entity>();
        foreach (var entry in world.View<TestComponentA, TestComponentB, TestComponentC>())
        {
            entities.Add(entry.Entity);
        }

        Assert.That(entities, Is.EquivalentTo(new[] { included }));
    }

    [Test]
    public void GetAllEntities_ClearsDestinationAndReturnsAliveEntities()
    {
        var world = new World(WorldTag.All);
        var first = world.CreateEntity();
        var second = world.CreateEntity();
        var entities = new List<Entity> { new(-1, -1) };

        world.GetAllEntities(entities);

        Assert.That(entities, Is.EquivalentTo(new[] { first, second }));
    }

    [Test]
    public void IsEnabled_ReturnsCurrentEnabledState()
    {
        var world = new World(WorldTag.All);
        var entity = world.CreateEntity();

        var isEnabled = world.IsEnabled(entity);

        Assert.That(isEnabled, Is.True);
    }

    [Test]
    public void SetEnabled_UpdatesEnabledState()
    {
        var world = new World(WorldTag.All);
        var entity = world.CreateEntity();

        world.SetEnabled(entity, false);

        Assert.That(world.IsEnabled(entity), Is.False);
    }

    [Test]
    public void GetAllComponents_ReturnsComponentsForAliveEntity()
    {
        var world = new World(WorldTag.All);
        var entity = world.CreateEntity("Entity");
        world.AddComponent(entity, new TestComponentA { Value = 4 });
        var components = new List<IEntityComponent> { new TestComponentB() };

        world.GetAllComponents(entity, components);

        Assert.That(components.Count, Is.EqualTo(2));
        Assert.That(components.OfType<NameComponent>().Single().Name, Is.EqualTo("Entity"));
        Assert.That(components.OfType<TestComponentA>().Single().Value, Is.EqualTo(4));
    }

    [Test]
    public void GetComponentTypes_ReturnsTypesForAliveEntity()
    {
        var world = new World(WorldTag.All);
        var entity = world.CreateEntity("Entity");
        world.AddComponent(entity, new TestComponentA { Value = 2 });
        var componentTypes = new List<Type> { typeof(TestComponentB) };

        world.GetComponentTypes(entity, componentTypes);

        Assert.That(componentTypes, Is.EquivalentTo(new[] { typeof(NameComponent), typeof(TestComponentA) }));
    }

    [Test]
    public void AddTransform_WithLocalTransform_AddsTransformComponents()
    {
        var world = new World(WorldTag.All);
        var entity = world.CreateEntity();

        world.AddTransform(entity, default(LocalTransform));

        Assert.That(world.HasComponent<LocalTransform>(entity), Is.True);
        Assert.That(world.HasComponent<WorldTransform>(entity), Is.True);
        Assert.That(world.HasComponent<DirtyTransformRoot>(entity), Is.True);
    }

    [Test]
    public void AddTransform_WithMatrix_AddsTransformComponents()
    {
        var world = new World(WorldTag.All);
        var entity = world.CreateEntity();

        world.AddTransform(entity, Matrix4x4.Identity);

        Assert.That(world.HasComponent<LocalTransform>(entity), Is.True);
        Assert.That(world.HasComponent<WorldTransform>(entity), Is.True);
        Assert.That(world.HasComponent<DirtyTransformRoot>(entity), Is.True);
    }

    [Test]
    public void SetParent_AddsHierarchyLinks()
    {
        var world = new World(WorldTag.All);
        var parent = CreateTransformEntity(world, "Parent", new Vector3(1.0f, 0.0f, 0.0f));
        var child = CreateTransformEntity(world, "Child", new Vector3(2.0f, 0.0f, 0.0f));

        world.SetParent(child, parent);

        Assert.That(world.GetComponent<Parent>(child).Value, Is.EqualTo(parent));
        Assert.That(world.GetComponent<Children>(parent).First, Is.EqualTo(child));
    }

    [Test]
    public void RemoveParent_RemovesHierarchyLinks()
    {
        var world = new World(WorldTag.All);
        var parent = CreateTransformEntity(world, "Parent", new Vector3(1.0f, 0.0f, 0.0f));
        var child = CreateTransformEntity(world, "Child", new Vector3(2.0f, 0.0f, 0.0f));
        world.SetParent(child, parent);

        world.RemoveParent(child);

        Assert.That(world.HasComponent<Parent>(child), Is.False);
        Assert.That(world.HasComponent<Children>(parent), Is.False);
        Assert.That(world.HasComponent<Sibling>(child), Is.False);
    }

    [Test]
    public void MarkDirty_AddsOnlyChangedEntityAsDirtyRoot()
    {
        var world = new World(WorldTag.All);
        var transformSystem = new TransformSystem();
        var parent = CreateTransformEntity(world, "Parent", new Vector3(1.0f, 0.0f, 0.0f));
        var child = CreateTransformEntity(world, "Child", new Vector3(2.0f, 0.0f, 0.0f));
        transformSystem.PreRender(0.0f, world);
        world.SetParent(child, parent);
        transformSystem.PreRender(0.0f, world);

        world.MarkDirty(child);

        Assert.That(world.HasComponent<DirtyTransformRoot>(parent), Is.False);
        Assert.That(world.HasComponent<DirtyTransformRoot>(child), Is.True);
        Assert.That(world.GetComponent<LocalTransform>(child).IsDirty, Is.True);
    }

    [Test]
    public void Translate_WhenLocal_OffsetsLocalPosition()
    {
        var world = new World(WorldTag.All);
        var entity = CreateTransformEntity(world, "Entity", Vector3.Zero);

        world.Translate(entity, new Vector3(1.0f, 2.0f, 3.0f), true);

        AssertVector3(world.GetComponent<LocalTransform>(entity).LocalPosition, new Vector3(1.0f, 2.0f, 3.0f));
    }

    [Test]
    public void SetLocalPosition_UpdatesLocalPosition()
    {
        var world = new World(WorldTag.All);
        var entity = CreateTransformEntity(world, "Entity", Vector3.Zero);

        world.SetLocalPosition(entity, new Vector3(4.0f, 5.0f, 6.0f));

        AssertVector3(world.GetComponent<LocalTransform>(entity).LocalPosition, new Vector3(4.0f, 5.0f, 6.0f));
    }

    [Test]
    public void SetLocalRotation_UpdatesLocalRotation()
    {
        var world = new World(WorldTag.All);
        var entity = CreateTransformEntity(world, "Entity", Vector3.Zero);
        var rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.2f, -0.1f, 0.3f));

        world.SetLocalRotation(entity, rotation);

        AssertQuaternion(world.GetComponent<LocalTransform>(entity).LocalRotation, rotation);
    }

    [Test]
    public void SetLocalScale_UpdatesLocalScale()
    {
        var world = new World(WorldTag.All);
        var entity = CreateTransformEntity(world, "Entity", Vector3.Zero);

        world.SetLocalScale(entity, new Vector3(7.0f, 8.0f, 9.0f));

        AssertVector3(world.GetComponent<LocalTransform>(entity).LocalScale, new Vector3(7.0f, 8.0f, 9.0f));
    }

    [Test]
    public void SetWorldPosition_ConvertsFromParentWorldSpace()
    {
        var world = new World(WorldTag.All);
        var transformSystem = new TransformSystem();
        var parent = CreateTransformEntity(world, "Parent", new Vector3(10.0f, 0.0f, 0.0f));
        var child = CreateTransformEntity(world, "Child", Vector3.Zero);
        transformSystem.PreRender(0.0f, world);
        world.SetParent(child, parent);
        transformSystem.PreRender(0.0f, world);

        world.SetWorldPosition(child, new Vector3(13.0f, 0.0f, 0.0f));

        AssertVector3(world.GetComponent<LocalTransform>(child).LocalPosition, new Vector3(3.0f, 0.0f, 0.0f));
    }

    [Test]
    public void SetWorldRotation_UpdatesLocalRotation()
    {
        var world = new World(WorldTag.All);
        var transformSystem = new TransformSystem();
        var entity = CreateTransformEntity(world, "Entity", Vector3.Zero);
        var rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.3f, 0.1f, -0.2f));
        transformSystem.PreRender(0.0f, world);

        world.SetWorldRotation(entity, rotation);

        AssertQuaternion(world.GetComponent<LocalTransform>(entity).LocalRotation, rotation);
    }

    [Test]
    public void SetWorldScale_UpdatesLocalScale()
    {
        var world = new World(WorldTag.All);
        var transformSystem = new TransformSystem();
        var entity = CreateTransformEntity(world, "Entity", Vector3.Zero);
        var scale = new Vector3(2.0f, 3.0f, 4.0f);
        transformSystem.PreRender(0.0f, world);

        world.SetWorldScale(entity, scale);

        AssertVector3(world.GetComponent<LocalTransform>(entity).LocalScale, scale);
    }

    [Test]
    public void ChainedWorldSetters_UsePendingLocalTransformInsteadOfStaleWorldTransform()
    {
        var world = new World(WorldTag.All);
        var transformSystem = new TransformSystem();
        var entity = CreateTransformEntity(world, "Entity", Vector3.Zero);
        transformSystem.PreRender(0.0f, world);
        var cachedWorldTransform = world.GetComponent<WorldTransform>(entity).LocalToWorld;
        var position = new Vector3(4.0f, 5.0f, 6.0f);
        var rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.3f, -0.2f, 0.1f));

        world.SetWorldPosition(entity, position);
        world.SetWorldRotation(entity, rotation);

        AssertVector3(world.GetComponent<LocalTransform>(entity).LocalPosition, position);
        AssertQuaternion(world.GetComponent<LocalTransform>(entity).LocalRotation, rotation);
        Assert.That(world.GetComponent<WorldTransform>(entity).LocalToWorld, Is.EqualTo(cachedWorldTransform));

        transformSystem.PreRender(0.0f, world);
        Assert.That(Matrix4x4.Decompose(world.GetComponent<WorldTransform>(entity).LocalToWorld, out _, out var resolvedRotation, out var resolvedPosition), Is.True);
        AssertVector3(resolvedPosition, position);
        AssertQuaternion(resolvedRotation, rotation);
    }

    [Test]
    public void SetWorldTransform_ResolvesDirtyParentFromLocalTransform()
    {
        var world = new World(WorldTag.All);
        var transformSystem = new TransformSystem();
        var parent = CreateTransformEntity(world, "Parent", Vector3.Zero);
        var child = CreateTransformEntity(world, "Child", Vector3.Zero);
        world.SetParent(child, parent);
        transformSystem.PreRender(0.0f, world);
        world.SetLocalPosition(parent, new Vector3(10.0f, 0.0f, 0.0f));

        world.SetWorldTransform(child, position: new Vector3(13.0f, 0.0f, 0.0f));

        AssertVector3(world.GetComponent<LocalTransform>(child).LocalPosition, new Vector3(3.0f, 0.0f, 0.0f));
    }

    private static Entity CreateTransformEntity(World world, string name, Vector3 position)
    {
        return world.CreateEntity(
            name,
            position,
            Quaternion.Identity,
            Vector3.One);
    }

    private static void AssertVector3(Vector3 actual, Vector3 expected, float tolerance = 0.0001f)
    {
        Assert.That(actual.X, Is.EqualTo(expected.X).Within(tolerance));
        Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(tolerance));
        Assert.That(actual.Z, Is.EqualTo(expected.Z).Within(tolerance));
    }

    private static void AssertQuaternion(Quaternion actual, Quaternion expected, float tolerance = 0.0001f)
    {
        Assert.That(actual.X, Is.EqualTo(expected.X).Within(tolerance));
        Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(tolerance));
        Assert.That(actual.Z, Is.EqualTo(expected.Z).Within(tolerance));
        Assert.That(actual.W, Is.EqualTo(expected.W).Within(tolerance));
    }
}

public struct TestComponentA : IEntityComponent
{
    public int Value;
}

public struct TestComponentB : IEntityComponent
{
    public int Value;
}

public struct TestComponentC : IEntityComponent
{
    public int Value;
}
