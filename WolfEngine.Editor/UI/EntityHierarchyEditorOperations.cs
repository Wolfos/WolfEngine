using System.Numerics;
using WolfEngine.ECS;

namespace WolfEngine.Editor.UI;

public readonly record struct EntityHierarchySnapshot(
	Guid EntityId,
	Guid? ParentEntityId,
	Matrix4x4? LocalTransform);

internal static class EntityHierarchyEditorOperations
{
	public static Entity? DuplicateEntity(
		EditorScene scene,
		Entity entity,
		IEditorSceneSnapshotService sceneSnapshotService,
		IEditorUndoRedoService undoRedoService,
		IEditorInteractionState interactionState)
	{
		ArgumentNullException.ThrowIfNull(scene);
		ArgumentNullException.ThrowIfNull(sceneSnapshotService);
		ArgumentNullException.ThrowIfNull(undoRedoService);
		ArgumentNullException.ThrowIfNull(interactionState);

		var world = scene.World;
		if (world.IsAlive(entity) == false || EditorPrefabUtility.IsNestedPrefabEntity(scene, entity))
		{
			return null;
		}

		var entitiesToDuplicate = new List<Entity>();
		CollectEntitySubtree(entity, world, entitiesToDuplicate);
		var snapshots = sceneSnapshotService.CaptureDeletedEntities(scene, entitiesToDuplicate);
		if (snapshots.Count == 0)
		{
			return null;
		}

		var duplicatedSnapshots = CloneForDuplication(snapshots);
		sceneSnapshotService.RestoreDeletedEntities(scene, duplicatedSnapshots);
		if (TryFindEntity(scene, duplicatedSnapshots[0].Entity.EntityId, out var duplicatedRoot) == false)
		{
			return null;
		}

		EditorGui.SelectEntity(duplicatedRoot, world, requestFocus: false);
		undoRedoService.BeginCapture("Duplicate Entity");
		undoRedoService.CommitCapture(new EntityCreationUndoRedoEntry("Duplicate Entity", duplicatedSnapshots));
		interactionState.MarkSceneDirty(scene.World);
		return duplicatedRoot;
	}

	public static bool TryReparentEntity(
		EditorScene scene,
		Entity entity,
		Entity? parent,
		IEditorSceneSnapshotService sceneSnapshotService,
		IEditorUndoRedoService undoRedoService,
		IEditorInteractionState interactionState)
	{
		return TryReparentEntities(scene, [entity], parent, sceneSnapshotService, undoRedoService, interactionState);
	}

	/// <summary>
	/// Moves <paramref name="entities"/> under <paramref name="parent"/> (or to the root when null) as one
	/// undo step, preserving each entity's world transform. Entities that cannot move — nested prefab
	/// entities, or ones already under <paramref name="parent"/> — are skipped. The whole move is refused
	/// when <paramref name="parent"/> is one of the moved entities or sits inside one of their subtrees.
	/// </summary>
	public static bool TryReparentEntities(
		EditorScene scene,
		IReadOnlyList<Entity> entities,
		Entity? parent,
		IEditorSceneSnapshotService sceneSnapshotService,
		IEditorUndoRedoService undoRedoService,
		IEditorInteractionState interactionState)
	{
		ArgumentNullException.ThrowIfNull(scene);
		ArgumentNullException.ThrowIfNull(entities);
		ArgumentNullException.ThrowIfNull(sceneSnapshotService);
		ArgumentNullException.ThrowIfNull(undoRedoService);
		ArgumentNullException.ThrowIfNull(interactionState);

		var world = scene.World;
		if (parent is { } parentEntity &&
		    (world.IsAlive(parentEntity) == false || EditorPrefabUtility.IsPrefabEntity(scene, parentEntity)))
		{
			return false;
		}

		var movedEntities = new List<Entity>(entities.Count);
		for (var i = 0; i < entities.Count; i++)
		{
			var entity = entities[i];
			if (world.IsAlive(entity) == false || movedEntities.Contains(entity))
			{
				continue;
			}

			if (parent is { } target && IsDescendantOf(world, target, entity))
			{
				return false;
			}

			if (EditorPrefabUtility.IsNestedPrefabEntity(scene, entity))
			{
				continue;
			}

			var alreadyInPlace = parent is { } newParent
				? IsSameParent(world, entity, newParent)
				: world.HasComponent<Parent>(entity) == false;
			if (alreadyInPlace == false)
			{
				movedEntities.Add(entity);
			}
		}

		// An entity inside another moved entity's subtree travels with that ancestor; moving it as well
		// would pull it out of the subtree.
		for (var i = movedEntities.Count - 1; i >= 0; i--)
		{
			if (HasAncestorIn(world, movedEntities[i], movedEntities))
			{
				movedEntities.RemoveAt(i);
			}
		}

		if (movedEntities.Count == 0)
		{
			return false;
		}

		var before = new EntityHierarchySnapshot[movedEntities.Count];
		var worldTransforms = new Matrix4x4?[movedEntities.Count];
		for (var i = 0; i < movedEntities.Count; i++)
		{
			var entity = movedEntities[i];
			before[i] = CaptureSnapshot(scene, entity, sceneSnapshotService);
			worldTransforms[i] = world.HasComponent<LocalTransform>(entity)
				? GetWorldTransform(world, entity)
				: null;
		}

		var after = new EntityHierarchySnapshot[movedEntities.Count];
		for (var i = 0; i < movedEntities.Count; i++)
		{
			var entity = movedEntities[i];
			ApplyParent(world, entity, parent);
			if (worldTransforms[i] is { } preservedWorldTransform && world.HasComponent<LocalTransform>(entity))
			{
				ApplyWorldTransform(world, entity, preservedWorldTransform);
			}

			after[i] = CaptureSnapshot(scene, entity, sceneSnapshotService);
		}

		var description = (parent is null ? "Unparent " : "Reparent ") +
		                  (movedEntities.Count == 1 ? "Entity" : "Entities");
		undoRedoService.BeginCapture(description);
		undoRedoService.CommitCapture(new EntityHierarchyUndoRedoEntry(description, before, after));

		EditorGui.ReplaceEntitySelection(movedEntities[0], world, requestFocus: false);
		for (var i = 1; i < movedEntities.Count; i++)
		{
			EditorGui.AddEntitySelection(movedEntities[i], world, requestFocus: false);
		}

		interactionState.MarkSceneDirty(scene.World);
		return true;
	}

	public static void ApplySnapshot(EditorScene scene, EntityHierarchySnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(scene);

		var world = scene.World;
		if (TryFindEntity(scene, snapshot.EntityId, out var entity) == false)
		{
			return;
		}

		if (snapshot.ParentEntityId is { } parentEntityId && TryFindEntity(scene, parentEntityId, out var parent))
		{
			ApplyParent(world, entity, parent);
		}
		else
		{
			ApplyParent(world, entity, null);
		}

		if (snapshot.LocalTransform is { } localTransform)
		{
			ApplyLocalTransform(world, entity, localTransform);
		}
	}

	private static EntityHierarchySnapshot CaptureSnapshot(
		EditorScene scene,
		Entity entity,
		IEditorSceneSnapshotService sceneSnapshotService)
	{
		var parentEntityId = scene.World.HasComponent<Parent>(entity)
			? sceneSnapshotService.EnsurePersistentEntityId(scene, scene.World.GetComponent<Parent>(entity).Value)
			: (Guid?)null;
		var localTransform = scene.World.HasComponent<LocalTransform>(entity)
			? scene.World.GetComponent<LocalTransform>(entity).GetTransform()
			: (Matrix4x4?)null;

		return new EntityHierarchySnapshot(
			sceneSnapshotService.EnsurePersistentEntityId(scene, entity),
			parentEntityId,
			localTransform);
	}

	private static bool IsSameParent(World world, Entity entity, Entity parent)
	{
		return world.HasComponent<Parent>(entity) && world.GetComponent<Parent>(entity).Value == parent;
	}

	private static bool IsDescendantOf(World world, Entity entity, Entity ancestor)
	{
		var visited = new HashSet<Entity>();
		var current = entity;
		while (current.IsValid && world.IsAlive(current) && visited.Add(current))
		{
			if (current == ancestor)
			{
				return true;
			}

			if (world.HasComponent<Parent>(current) == false)
			{
				break;
			}

			current = world.GetComponent<Parent>(current).Value;
		}

		return false;
	}

	private static bool HasAncestorIn(World world, Entity entity, List<Entity> candidates)
	{
		if (world.HasComponent<Parent>(entity) == false)
		{
			return false;
		}

		var parent = world.GetComponent<Parent>(entity).Value;
		for (var i = 0; i < candidates.Count; i++)
		{
			if (candidates[i] != entity && IsDescendantOf(world, parent, candidates[i]))
			{
				return true;
			}
		}

		return false;
	}

	private static void ApplyParent(World world, Entity entity, Entity? parent)
	{
		if (parent is { } parentEntity)
		{
			world.SetParent(entity, parentEntity);
		}
		else
		{
			world.RemoveParent(entity);
		}
	}

	private static Matrix4x4 GetWorldTransform(World world, Entity entity)
	{
		var localTransform = world.HasComponent<LocalTransform>(entity)
			? world.GetComponent<LocalTransform>(entity).GetTransform()
			: Matrix4x4.Identity;

		if (world.HasComponent<Parent>(entity) == false)
		{
			return localTransform;
		}

		var parent = world.GetComponent<Parent>(entity).Value;
		if (parent.IsValid == false || world.IsAlive(parent) == false)
		{
			return localTransform;
		}

		return localTransform * GetWorldTransform(world, parent);
	}

	private static void ApplyWorldTransform(World world, Entity entity, in Matrix4x4 worldTransform)
	{
		var parentWorldTransform = Matrix4x4.Identity;
		if (world.HasComponent<Parent>(entity))
		{
			var parent = world.GetComponent<Parent>(entity).Value;
			if (parent.IsValid && world.IsAlive(parent))
			{
				parentWorldTransform = GetWorldTransform(world, parent);
			}
		}

		if (Matrix4x4.Invert(parentWorldTransform, out var parentWorldToLocal) == false)
		{
			parentWorldToLocal = Matrix4x4.Identity;
		}

		ApplyLocalTransform(world, entity, worldTransform * parentWorldToLocal);
	}

	private static void ApplyLocalTransform(World world, Entity entity, in Matrix4x4 localTransform)
	{
		if (world.HasComponent<LocalTransform>(entity) == false)
		{
			world.AddTransform(entity, localTransform);
			return;
		}

		if (Matrix4x4.Decompose(localTransform, out var scale, out var rotation, out var position) == false)
		{
			return;
		}

		world.SetLocalPosition(entity, position);
		world.SetLocalRotation(entity, rotation.LengthSquared() > 0.0f ? Quaternion.Normalize(rotation) : Quaternion.Identity);
		world.SetLocalScale(entity, scale);
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

	private static IReadOnlyList<DeletedEntitySnapshot> CloneForDuplication(IReadOnlyList<DeletedEntitySnapshot> snapshots)
	{
		var idMap = new Dictionary<Guid, Guid>(snapshots.Count);
		for (var i = 0; i < snapshots.Count; i++)
		{
			var originalId = snapshots[i].Entity.EntityId;
			if (originalId != Guid.Empty)
			{
				idMap[originalId] = Guid.NewGuid();
			}
		}

		var clones = new List<DeletedEntitySnapshot>(snapshots.Count);
		for (var i = 0; i < snapshots.Count; i++)
		{
			var snapshot = snapshots[i];
			var clonedEntity = EditorPrefabUtility.CloneEntity(snapshot.Entity);
			clonedEntity.EntityId = idMap[clonedEntity.EntityId];
			if (clonedEntity.ParentEntityId is { } parentEntityId && idMap.TryGetValue(parentEntityId, out var duplicatedParentId))
			{
				clonedEntity.ParentEntityId = duplicatedParentId;
			}

			// Point references that targeted the duplicated subtree at the copies instead of the originals.
			for (var componentIndex = 0; componentIndex < clonedEntity.Components.Count; componentIndex++)
			{
				var component = clonedEntity.Components[componentIndex];
				component.Data = EditorEntityReferenceUtility.RemapEntityReferences(component.Data, idMap);
			}

			clones.Add(new DeletedEntitySnapshot(snapshot.CellKey, clonedEntity));
		}

		return clones;
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
}
