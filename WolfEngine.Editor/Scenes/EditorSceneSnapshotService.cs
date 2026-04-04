using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;

namespace WolfEngine.Editor;

public readonly record struct SceneComponentSnapshot(
	Guid EntityId,
	string ComponentType,
	string ComponentTypeId,
	JsonElement Data);

public readonly record struct DeletedEntitySnapshot(
	SceneCellKey CellKey,
	SavedEntity Entity);

public interface IEditorSceneSnapshotService
{
	SceneComponentSnapshot CaptureComponent(EditorScene scene, Entity entity, Type componentType);
	IReadOnlyList<DeletedEntitySnapshot> CaptureDeletedEntities(EditorScene scene, IReadOnlyList<Entity> entities);
	void ApplyComponentSnapshots(EditorScene scene, IReadOnlyList<SceneComponentSnapshot> snapshots);
	void RestoreDeletedEntities(EditorScene scene, IReadOnlyList<DeletedEntitySnapshot> deletedEntities);
	void DeleteEntitiesByPersistentIds(EditorScene scene, IReadOnlyList<Guid> entityIds);
	Guid EnsurePersistentEntityId(EditorScene scene, Entity entity);
}

public sealed class EditorSceneSnapshotService : IEditorSceneSnapshotService
{
	private readonly IProjectTypeResolver _typeResolver;

	public EditorSceneSnapshotService(IProjectTypeResolver typeResolver)
	{
		_typeResolver = typeResolver ?? throw new ArgumentNullException(nameof(typeResolver));
	}

	public SceneComponentSnapshot CaptureComponent(EditorScene scene, Entity entity, Type componentType)
	{
		ArgumentNullException.ThrowIfNull(scene);
		ArgumentNullException.ThrowIfNull(componentType);
		var entityId = EnsurePersistentEntityId(scene, entity);
		var data = componentType == typeof(LocalTransform)
			? JsonSerializer.SerializeToElement(scene.World.GetComponent<LocalTransform>(entity).GetTransform(), AssetJson.SerializerOptions)
			: JsonSerializer.SerializeToElement(
				RuntimeComponentAccessor.ReadBoxed(scene.World, entity, componentType),
				componentType,
				AssetJson.GetSerializerOptions(componentType));

		return new SceneComponentSnapshot(
			entityId,
			_typeResolver.GetTypeName(componentType),
			_typeResolver.GetStableTypeId(componentType),
			data.Clone());
	}

	public IReadOnlyList<DeletedEntitySnapshot> CaptureDeletedEntities(EditorScene scene, IReadOnlyList<Entity> entities)
	{
		ArgumentNullException.ThrowIfNull(scene);
		ArgumentNullException.ThrowIfNull(entities);

		var snapshots = new List<DeletedEntitySnapshot>(entities.Count);
		for (var i = 0; i < entities.Count; i++)
		{
			var entity = entities[i];
			if (scene.World.IsAlive(entity) == false)
			{
				continue;
			}

			var entityId = EnsurePersistentEntityId(scene, entity);
			var cellKey = scene.EntityCellKeys.TryGetValue(entity, out var storedCellKey)
				? storedCellKey
				: SceneCellKey.Global;
			snapshots.Add(new DeletedEntitySnapshot(cellKey, SerializeEntity(scene, entity, entityId)));
		}

		return snapshots;
	}

	public void ApplyComponentSnapshots(EditorScene scene, IReadOnlyList<SceneComponentSnapshot> snapshots)
	{
		ArgumentNullException.ThrowIfNull(scene);
		ArgumentNullException.ThrowIfNull(snapshots);

		for (var i = 0; i < snapshots.Count; i++)
		{
			var snapshot = snapshots[i];
			if (TryFindEntity(scene, snapshot.EntityId, out var entity) == false ||
			    TryResolveComponentType(snapshot, out var componentType) == false)
			{
				continue;
			}

			if (componentType == typeof(LocalTransform))
			{
				var transform = snapshot.Data.Deserialize<Matrix4x4>(AssetJson.SerializerOptions);
				ApplyLocalTransform(scene.World, entity, transform);
				continue;
			}

			var componentValue = ProjectTypeStateTransferUtility.DeserializeWithFieldMerge(snapshot.Data, componentType);
			RuntimeComponentAccessor.WriteBoxed(scene.World, entity, componentType, componentValue);
		}
	}

	public void RestoreDeletedEntities(EditorScene scene, IReadOnlyList<DeletedEntitySnapshot> deletedEntities)
	{
		ArgumentNullException.ThrowIfNull(scene);
		ArgumentNullException.ThrowIfNull(deletedEntities);

		var entitiesById = new Dictionary<Guid, Entity>(deletedEntities.Count);
		for (var i = 0; i < deletedEntities.Count; i++)
		{
			var snapshot = deletedEntities[i];
			if (snapshot.Entity.EntityId == Guid.Empty)
			{
				continue;
			}

			if (TryFindEntity(scene, snapshot.Entity.EntityId, out var existing))
			{
				entitiesById[snapshot.Entity.EntityId] = existing;
				continue;
			}

				var entity = CreateEntity(scene.World, snapshot.Entity);
				scene.EntityIds[entity] = snapshot.Entity.EntityId;
				scene.EntityCellKeys[entity] = snapshot.CellKey;
				if (snapshot.Entity.PrefabSourcePath.Count > 0)
				{
					scene.EntityPrefabSourcePaths[entity] = EditorPrefabUtility.ClonePrefabSourcePath(snapshot.Entity.PrefabSourcePath);
				}

				if (string.IsNullOrWhiteSpace(snapshot.Entity.Icon) == false)
				{
					scene.EntityIcons[entity] = snapshot.Entity.Icon;
			}

			scene.World.SetEnabled(entity, snapshot.Entity.Enabled);
			for (var componentIndex = 0; componentIndex < snapshot.Entity.Components.Count; componentIndex++)
			{
				ApplySavedComponent(scene.World, entity, snapshot.Entity.Components[componentIndex]);
			}

			entitiesById[snapshot.Entity.EntityId] = entity;
		}

		for (var i = 0; i < deletedEntities.Count; i++)
		{
			var snapshot = deletedEntities[i];
			if (snapshot.Entity.ParentEntityId is not { } parentEntityId ||
			    entitiesById.TryGetValue(snapshot.Entity.EntityId, out var entity) == false ||
			    entitiesById.TryGetValue(parentEntityId, out var parent) == false)
			{
				continue;
			}

			scene.World.SetParent(entity, parent);
		}
	}

	public void DeleteEntitiesByPersistentIds(EditorScene scene, IReadOnlyList<Guid> entityIds)
	{
		ArgumentNullException.ThrowIfNull(scene);
		ArgumentNullException.ThrowIfNull(entityIds);

		for (var i = 0; i < entityIds.Count; i++)
		{
			if (TryFindEntity(scene, entityIds[i], out var entity) == false)
			{
				continue;
			}

			DeleteEntity(scene, entity);
		}
	}

	public Guid EnsurePersistentEntityId(EditorScene scene, Entity entity)
	{
		ArgumentNullException.ThrowIfNull(scene);
		if (scene.EntityIds.TryGetValue(entity, out var entityId) && entityId != Guid.Empty)
		{
			return entityId;
		}

		entityId = Guid.NewGuid();
		scene.EntityIds[entity] = entityId;
		return entityId;
	}

	private SavedEntity SerializeEntity(EditorScene scene, Entity entity, Guid entityId)
	{
		var world = scene.World;
		var hasName = world.HasComponent<NameComponent>(entity);
		var savedEntity = new SavedEntity
		{
			EntityId = entityId,
			ParentEntityId = TryGetParentEntityId(scene, entity),
			HasName = hasName,
			Name = hasName ? world.GetComponent<NameComponent>(entity).Name ?? string.Empty : string.Empty,
			Enabled = world.IsEnabled(entity),
				Icon = scene.EntityIcons.TryGetValue(entity, out var iconName) ? iconName : string.Empty,
				LocalTransform = world.HasComponent<LocalTransform>(entity)
					? world.GetComponent<LocalTransform>(entity).GetTransform()
					: null,
				PrefabSourcePath = scene.EntityPrefabSourcePaths.TryGetValue(entity, out var prefabSourcePath)
					? EditorPrefabUtility.ClonePrefabSourcePath(prefabSourcePath)
					: [],
				Components = []
			};

		var componentTypes = new List<Type>();
		world.GetComponentTypes(entity, componentTypes);
		for (var i = 0; i < componentTypes.Count; i++)
		{
			var componentType = componentTypes[i];
			if (IsPersistableComponentType(componentType) == false)
			{
				continue;
			}

			savedEntity.Components.Add(new SavedComponent
			{
				Type = _typeResolver.GetTypeName(componentType),
				TypeId = _typeResolver.GetStableTypeId(componentType),
				Data = JsonSerializer.SerializeToElement(
					RuntimeComponentAccessor.ReadBoxed(world, entity, componentType),
					componentType,
					AssetJson.GetSerializerOptions(componentType))
			});
		}

		return CloneEntity(savedEntity);
	}

	private void ApplySavedComponent(World world, Entity entity, SavedComponent component)
	{
		if (TryResolveComponentType(component.Type, component.TypeId, out var componentType) == false ||
		    IsPersistableComponentType(componentType) == false)
		{
			return;
		}

		var componentValue = ProjectTypeStateTransferUtility.DeserializeWithFieldMerge(component.Data, componentType);
		RuntimeComponentAccessor.WriteBoxed(world, entity, componentType, componentValue);
	}

	private bool TryResolveComponentType(SceneComponentSnapshot snapshot, out Type componentType)
	{
		return TryResolveComponentType(snapshot.ComponentType, snapshot.ComponentTypeId, out componentType);
	}

	private bool TryResolveComponentType(string typeName, string typeId, out Type componentType)
	{
		if (_typeResolver.TryResolveStableTypeId(typeId, out componentType))
		{
			return true;
		}

		if (_typeResolver.TryResolveType(typeName, out componentType))
		{
			return true;
		}

		return ProjectTypeResolverUtility.TryResolveFromLoadedAssemblies(typeName, out componentType);
	}

	private static bool TryFindEntity(EditorScene scene, Guid entityId, out Entity entity)
	{
		foreach (var entry in scene.EntityIds)
		{
			if (entry.Value == entityId && scene.World.IsAlive(entry.Key))
			{
				entity = entry.Key;
				return true;
			}
		}

		entity = default;
		return false;
	}

	private static Guid? TryGetParentEntityId(EditorScene scene, Entity entity)
	{
		if (scene.World.HasComponent<Parent>(entity) == false)
		{
			return null;
		}

		var parent = scene.World.GetComponent<Parent>(entity).Value;
		return scene.EntityIds.TryGetValue(parent, out var parentId) && parentId != Guid.Empty
			? parentId
			: null;
	}

	private static void ApplyLocalTransform(World world, Entity entity, Matrix4x4 transform)
	{
		if (world.HasComponent<LocalTransform>(entity) == false)
		{
			world.AddTransform(entity, transform);
			return;
		}

		Matrix4x4.Decompose(transform, out var scale, out var rotation, out var position);
		world.SetLocalPosition(entity, position);
		world.SetLocalRotation(entity, rotation);
		world.SetLocalScale(entity, scale);
	}

	private static bool IsPersistableComponentType(Type componentType)
	{
		return componentType == typeof(NameComponent) == false
		       && componentType.IsValueType
		       && typeof(IEntityComponent).IsAssignableFrom(componentType)
		       && Attribute.IsDefined(componentType, typeof(NotSerializedAttribute)) == false
		       && Attribute.IsDefined(componentType, typeof(ExcludeFromEditorAttribute)) == false
		       && Attribute.IsDefined(componentType, typeof(EditorOnlyAttribute)) == false;
	}

	private static Entity CreateEntity(World world, SavedEntity savedEntity)
	{
		if (savedEntity.HasName && savedEntity.LocalTransform is { } transformWithName)
		{
			return world.CreateEntity(savedEntity.Name, transformWithName);
		}

		if (savedEntity.HasName)
		{
			return world.CreateEntity(savedEntity.Name);
		}

		var entity = world.CreateEntity();
		if (savedEntity.LocalTransform is { } transform)
		{
			world.AddTransform(entity, transform);
		}

		return entity;
	}

	private static void DeleteEntity(EditorScene scene, Entity entity)
	{
		if (scene.World.IsAlive(entity) == false)
		{
			return;
		}

		var entities = new List<Entity>();
		CollectEntitySubtree(entity, scene.World, entities);
		scene.World.DestroyEntity(entity);
		for (var i = 0; i < entities.Count; i++)
		{
				var deletedEntity = entities[i];
				scene.EntityIcons.Remove(deletedEntity);
				scene.EntityCellKeys.Remove(deletedEntity);
				scene.EntityIds.Remove(deletedEntity);
				scene.EntityPrefabSourcePaths.Remove(deletedEntity);
			}

		if (EditorGui.HasSelectedEntity && entities.Contains(EditorGui.SelectedEntity))
		{
			EditorGui.ClearEntitySelection();
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

	private static SavedEntity CloneEntity(SavedEntity source)
	{
		return EditorPrefabUtility.CloneEntity(source);
	}
}
