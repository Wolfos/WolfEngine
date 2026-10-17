using System.Numerics;
using NSubstitute;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class EditorSceneSnapshotServiceTests
{
	[TestCase(false)]
	[TestCase(true)]
	public void RestoreDeletedEntities_ReconnectsSubtreeToSurvivingParent(bool parentHasPersistentId)
	{
		var scene = new EditorScene();
		var service = new EditorSceneSnapshotService(CreateTypeResolver());
		var parent = scene.World.CreateEntity("Parent", Matrix4x4.CreateTranslation(10, 20, 30));
		if (parentHasPersistentId)
		{
			service.EnsurePersistentEntityId(scene, parent);
		}

		var localTransform = Matrix4x4.CreateTranslation(1, 2, 3);
		var child = scene.World.CreateEntity("Child", localTransform);
		var grandchild = scene.World.CreateEntity("Grandchild", Matrix4x4.Identity);
		scene.World.SetParent(child, parent);
		scene.World.SetParent(grandchild, child);

		// Capture descendants first to also cover parents whose ids are assigned later.
		var snapshots = service.CaptureDeletedEntities(scene, [grandchild, child]);
		Assert.That(snapshots[1].Entity.ParentEntityId, Is.EqualTo(scene.EntityIds[parent]));
		var ids = snapshots.Select(snapshot => snapshot.Entity.EntityId).ToArray();

		// Repeat deletion/restoration to exercise the same snapshots across redo/undo.
		for (var cycle = 0; cycle < 2; cycle++)
		{
			service.DeleteEntitiesByPersistentIds(scene, ids);
			Assert.That(scene.World.IsAlive(parent), Is.True);
			Assert.That(scene.World.HasComponent<Children>(parent), Is.False);
			service.RestoreDeletedEntities(scene, snapshots);

			var restoredChild = FindEntityByName(scene.World, "Child");
			var restoredGrandchild = FindEntityByName(scene.World, "Grandchild");
			Assert.That(scene.World.GetComponent<Parent>(restoredChild).Value, Is.EqualTo(parent));
			Assert.That(scene.World.GetComponent<Children>(parent).First, Is.EqualTo(restoredChild));
			Assert.That(scene.World.GetComponent<Parent>(restoredGrandchild).Value, Is.EqualTo(restoredChild));
			Assert.That(scene.World.GetComponent<Children>(restoredChild).First, Is.EqualTo(restoredGrandchild));
			Assert.That(scene.World.GetComponent<LocalTransform>(restoredChild).GetTransform(), Is.EqualTo(localTransform));
			Assert.That(scene.EntityIds[restoredChild], Is.EqualTo(snapshots[1].Entity.EntityId));
		}
	}

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
