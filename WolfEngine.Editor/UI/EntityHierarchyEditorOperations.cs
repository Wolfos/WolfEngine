using System;
using System.Collections.Generic;
using System.Numerics;
using WolfEngine.ECS;

namespace WolfEngine.Editor.UI;

public readonly record struct EntityHierarchySnapshot(
	Guid EntityId,
	Guid? ParentEntityId,
	Matrix4x4? LocalTransform);

internal static class EntityHierarchyEditorOperations
{
	public static bool TryReparentEntity(
		EditorScene scene,
		Entity entity,
		Entity? parent,
		IEditorSceneSnapshotService sceneSnapshotService,
		IEditorUndoRedoService undoRedoService,
		IEditorInteractionState interactionState)
	{
		ArgumentNullException.ThrowIfNull(scene);
		ArgumentNullException.ThrowIfNull(sceneSnapshotService);
		ArgumentNullException.ThrowIfNull(undoRedoService);
		ArgumentNullException.ThrowIfNull(interactionState);

		var world = scene.World;
		if (world.IsAlive(entity) == false)
		{
			return false;
		}

		if (parent is { } parentEntity)
		{
			if (world.IsAlive(parentEntity) == false ||
			    entity == parentEntity ||
			    IsSameParent(world, entity, parentEntity) ||
			    IsDescendantOf(world, parentEntity, entity))
			{
				return false;
			}
		}
		else if (world.HasComponent<Parent>(entity) == false)
		{
			return false;
		}

		var before = CaptureSnapshot(scene, entity, sceneSnapshotService);
		var worldTransform = world.HasComponent<LocalTransform>(entity)
			? GetWorldTransform(world, entity)
			: (Matrix4x4?)null;

		ApplyParent(world, entity, parent);
		if (worldTransform is { } preservedWorldTransform && world.HasComponent<LocalTransform>(entity))
		{
			ApplyWorldTransform(world, entity, preservedWorldTransform);
		}

		var after = CaptureSnapshot(scene, entity, sceneSnapshotService);
		undoRedoService.BeginCapture(parent is null ? "Unparent Entity" : "Reparent Entity");
		undoRedoService.CommitCapture(new EntityHierarchyUndoRedoEntry(
			parent is null ? "Unparent Entity" : "Reparent Entity",
			before,
			after));

		EditorGui.SelectEntity(entity, world, requestFocus: false);
		interactionState.MarkSceneDirty();
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
}
