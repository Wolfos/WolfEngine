using System.Numerics;
using NSubstitute;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class EditorSceneSnapshotServiceTests
{
	[Test]
	public void RestoreDeletedEntities_ResolvesReferenceToAnotherEntityInTheSameBatch()
	{
		var scene = new EditorScene();
		var service = new EditorSceneSnapshotService(CreateTypeResolver());
		var root = scene.World.CreateEntity("Root");
		scene.World.AddTransform(root, Matrix4x4.Identity);
		var child = scene.World.CreateEntity("Child");
		scene.World.AddTransform(child, Matrix4x4.Identity);
		scene.World.SetParent(child, root);
		var external = scene.World.CreateEntity("External");
		service.EnsurePersistentEntityId(scene, external);
		scene.World.AddComponent(root, new EntityReferenceComponent
		{
			Target = child,
			External = external
		});

		var snapshots = service.CaptureDeletedEntities(scene, [root, child]);
		DeleteEntity(scene, root);
		service.RestoreDeletedEntities(scene, snapshots);

		var restoredRoot = FindEntityByName(scene.World, "Root");
		var restoredChild = FindEntityByName(scene.World, "Child");
		var component = scene.World.GetComponent<EntityReferenceComponent>(restoredRoot);

		Assert.That(component.Target, Is.EqualTo(restoredChild));
		Assert.That(component.External, Is.EqualTo(external));
	}

	private static IProjectTypeResolver CreateTypeResolver()
	{
		var resolver = Substitute.For<IProjectTypeResolver>();
		resolver.GetTypeName(Arg.Any<Type>()).Returns(call => call.Arg<Type>().AssemblyQualifiedName);
		resolver.GetStableTypeId(Arg.Any<Type>()).Returns(call => call.Arg<Type>().FullName);
		return resolver;
	}

	private static void DeleteEntity(EditorScene scene, Entity entity)
	{
		var deleted = new List<Entity>();
		CollectEntitySubtree(entity, scene.World, deleted);
		scene.World.DestroyEntity(entity);
		for (var i = 0; i < deleted.Count; i++)
		{
			scene.EntityIcons.Remove(deleted[i]);
			scene.EntityCellKeys.Remove(deleted[i]);
			scene.EntityIds.Remove(deleted[i]);
			scene.EntityPrefabSourcePaths.Remove(deleted[i]);
		}
	}

	private static void CollectEntitySubtree(Entity entity, World world, List<Entity> entities)
	{
		entities.Add(entity);
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
			CollectEntitySubtree(child, world, entities);
			child = next;
		}
	}

	private static Entity FindEntityByName(World world, string name)
	{
		var entities = new List<Entity>();
		world.GetAllEntities(entities);
		for (var i = 0; i < entities.Count; i++)
		{
			var entity = entities[i];
			if (world.HasComponent<NameComponent>(entity) &&
			    string.Equals(world.GetComponent<NameComponent>(entity).Name, name, StringComparison.Ordinal))
			{
				return entity;
			}
		}

		throw new InvalidOperationException($"Entity '{name}' was not found.");
	}

	private struct EntityReferenceComponent : IEntityComponent
	{
		public Entity Target;
		public Entity External;
	}
}
