using System.Numerics;
using System.Reflection;
using System.Text.Json;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
using WolfEngine.Rendering;

namespace WolfEngine.Editor;

public enum EntityCreationPreset
{
	Empty,
	PointLight,
	DirectionalLight,
	Cube,
	Sphere
}

public static class EntityCreationPresets
{
	public static string GetDisplayName(EntityCreationPreset preset) => preset switch
	{
		EntityCreationPreset.Empty => "Entity",
		EntityCreationPreset.PointLight => "Point Light",
		EntityCreationPreset.DirectionalLight => "Directional Light",
		EntityCreationPreset.Cube => "Cube",
		EntityCreationPreset.Sphere => "Sphere",
		_ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null)
	};
}

/// <summary>
/// The outcome of a scene-authoring operation. A refusal is an expected outcome, such as editing inside a
/// prefab instance, and carries a reason the caller can show; it is never thrown.
/// </summary>
public readonly record struct SceneAuthoringResult(string? RefusalReason)
{
	public bool Succeeded => RefusalReason is null;

	public static SceneAuthoringResult Success() => new(null);

	public static SceneAuthoringResult Refused(string reason) => new(reason);
}

/// <inheritdoc cref="SceneAuthoringResult"/>
public readonly record struct SceneAuthoringResult<T>(T Value, string? RefusalReason)
{
	public bool Succeeded => RefusalReason is null;

	public static SceneAuthoringResult<T> Success(T value) => new(value, null);

	public static SceneAuthoringResult<T> Refused(string reason) => new(default!, reason);
}

/// <summary>Where an entity sat in the hierarchy: its parent and its local transform under that parent.</summary>
public readonly record struct EntityHierarchySnapshot(
	Guid EntityId,
	Guid? ParentEntityId,
	Matrix4x4? LocalTransform);

/// <summary>The entities one instantiation created: the root first, then every entity under it.</summary>
public sealed record InstantiatedEntities(Entity Root, IReadOnlyList<Entity> Entities);

/// <summary>
/// One continuous change to one component on a fixed set of entities, such as an inspector drag. Nothing is
/// recorded until <see cref="Commit"/>, which records every entity whose component changed as one undo step;
/// <see cref="Cancel"/> puts every component back. An edit ends once, and using it afterwards throws.
/// </summary>
public interface ISceneComponentEdit
{
	/// <summary>The entities that had the component when the edit began and are still alive.</summary>
	IReadOnlyList<Entity> Entities { get; }

	Type ComponentType { get; }

	/// <summary>Runs <paramref name="change"/> once for each entity in the edit.</summary>
	void Apply(Action<Entity> change);

	/// <summary>Ends the edit as one undo step on the authoring scene. Returns whether any component changed.</summary>
	bool Commit();

	/// <summary>Ends the edit and restores every component to what it was when the edit began.</summary>
	void Cancel();
}

/// <summary>A component edit of <see cref="LocalTransform"/>, with the world-space setters a gizmo needs.</summary>
public interface ISceneTransformEdit : ISceneComponentEdit
{
	void SetWorldPosition(Entity entity, Vector3 worldPosition);

	void SetWorldRotation(Entity entity, Quaternion worldRotation);

	void SetLocalRotation(Entity entity, Quaternion localRotation);

	void SetLocalScale(Entity entity, Vector3 localScale);
}

/// <summary>
/// Changes a scene's entities the way a user would: create, duplicate, delete, instantiate, reparent and
/// move them. On the authoring scene every operation is one undo step and marks the scene dirty. On the
/// Play-mode runtime scene the same operations apply but are never recorded, because that scene is
/// discarded when Play stops. Callers own selection, except that a deleted entity never stays selected.
/// </summary>
public interface ISceneAuthoringService
{
	SceneAuthoringResult<Entity> CreateEntity(
		EditorScene scene,
		EntityCreationPreset preset,
		Vector3 position,
		string? name = null);

	/// <summary>Copies an entity and its subtree. Entity references inside the subtree point at the copies.</summary>
	SceneAuthoringResult<Entity> DuplicateEntity(EditorScene scene, Entity entity);

	/// <summary>Deletes each entity with its subtree, as one step.</summary>
	SceneAuthoringResult DeleteEntities(EditorScene scene, IReadOnlyList<Entity> entities);

	/// <summary>Places a 3D model or prefab from the open project's asset catalog, mounted assets included.</summary>
	SceneAuthoringResult<InstantiatedEntities> Instantiate(EditorScene scene, Guid assetId, Vector3? spawnPosition);

	/// <summary>
	/// Moves entities under <paramref name="parent"/>, or to the root when it is null, keeping their world
	/// transforms. Entities inside prefab instances, already in place, or travelling with a moved ancestor
	/// are skipped. Returns the entities that moved.
	/// </summary>
	SceneAuthoringResult<IReadOnlyList<Entity>> ReparentEntities(
		EditorScene scene,
		IReadOnlyList<Entity> entities,
		Entity? parent);

	ISceneTransformEdit BeginTransformEdit(EditorScene scene, IReadOnlyList<Entity> entities);

	/// <summary>
	/// Begins an edit of <paramref name="componentType"/> on every entity in <paramref name="entities"/> that
	/// has it. <paramref name="description"/> names the undo step.
	/// </summary>
	ISceneComponentEdit BeginComponentEdit(
		EditorScene scene,
		IReadOnlyList<Entity> entities,
		Type componentType,
		string description);

	/// <summary>Adds the component with its default values. The component's default icon is used when the entity has none.</summary>
	SceneAuthoringResult AddComponent(EditorScene scene, Entity entity, Type componentType);

	SceneAuthoringResult RemoveComponent(EditorScene scene, Entity entity, Type componentType);

	SceneAuthoringResult SetEnabled(EditorScene scene, Entity entity, bool enabled);

	SceneAuthoringResult SetIcon(EditorScene scene, Entity entity, string iconName);
}

public sealed class SceneAuthoringService : ISceneAuthoringService
{
	// Lights shine along local +Z. Pitching down keeps a new directional light clear of the horizon fade.
	private static readonly Quaternion DirectionalLightRotation =
		Quaternion.CreateFromAxisAngle(Vector3.UnitX, 50.0f * MathF.PI / 180.0f);

	private readonly IEditorSceneSnapshotService _sceneSnapshotService;
	private readonly IEditorUndoRedoService _undoRedoService;
	private readonly IEditorInteractionState _interactionState;
	private readonly IEditorProjectService _projectService;
	private readonly IProjectAssetPipelineService _assetPipelineService;

	public SceneAuthoringService(
		IEditorSceneSnapshotService sceneSnapshotService,
		IEditorUndoRedoService undoRedoService,
		IEditorInteractionState interactionState,
		IEditorProjectService projectService,
		IProjectAssetPipelineService assetPipelineService)
	{
		_sceneSnapshotService = sceneSnapshotService ?? throw new ArgumentNullException(nameof(sceneSnapshotService));
		_undoRedoService = undoRedoService ?? throw new ArgumentNullException(nameof(undoRedoService));
		_interactionState = interactionState ?? throw new ArgumentNullException(nameof(interactionState));
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_assetPipelineService = assetPipelineService ?? throw new ArgumentNullException(nameof(assetPipelineService));
	}

	public SceneAuthoringResult<Entity> CreateEntity(
		EditorScene scene,
		EntityCreationPreset preset,
		Vector3 position,
		string? name = null)
	{
		ArgumentNullException.ThrowIfNull(scene);

		var world = scene.World;
		var resolvedName = string.IsNullOrWhiteSpace(name)
			? EntityCreationPresets.GetDisplayName(preset)
			: name.Trim();
		var rotation = preset == EntityCreationPreset.DirectionalLight
			? DirectionalLightRotation
			: Quaternion.Identity;
		var entity = world.CreateEntity(resolvedName, position, rotation, Vector3.One);

		switch (preset)
		{
			case EntityCreationPreset.Empty:
				break;

			case EntityCreationPreset.PointLight:
				AddLight(scene, entity, new Light
				{
					Type = LightType.Point,
					Color = ColorRGBA.White,
					Intensity = 1.0f,
					Range = 10.0f
				});
				break;

			case EntityCreationPreset.DirectionalLight:
				AddLight(scene, entity, new Light
				{
					Type = LightType.Directional,
					Color = ColorRGBA.White,
					Intensity = 1.0f,
					Range = 25.0f,
					HorizonFade = true
				});
				break;

			case EntityCreationPreset.Cube:
				AddMeshRenderer(world, entity, BuiltInEngineAssets.CubeMesh);
				break;

			case EntityCreationPreset.Sphere:
				AddMeshRenderer(world, entity, BuiltInEngineAssets.SphereMesh);
				break;

			default:
				throw new ArgumentOutOfRangeException(nameof(preset), preset, null);
		}

		_sceneSnapshotService.EnsurePersistentEntityId(scene, entity);
		RecordCreation(scene, $"Create {resolvedName}", [entity]);

		return SceneAuthoringResult<Entity>.Success(entity);
	}

	public SceneAuthoringResult<Entity> DuplicateEntity(EditorScene scene, Entity entity)
	{
		ArgumentNullException.ThrowIfNull(scene);

		if (scene.World.IsAlive(entity) == false)
		{
			return SceneAuthoringResult<Entity>.Refused("The entity no longer exists.");
		}

		if (EditorPrefabUtility.IsNestedPrefabEntity(scene, entity))
		{
			return SceneAuthoringResult<Entity>.Refused(
				"Cannot duplicate entities inside prefab instances. Duplicate the prefab root instance instead.");
		}

		var entitiesToDuplicate = new List<Entity>();
		CollectEntitySubtree(entity, scene.World, entitiesToDuplicate);
		var snapshots = _sceneSnapshotService.CaptureDeletedEntities(scene, entitiesToDuplicate);
		if (snapshots.Count == 0)
		{
			return SceneAuthoringResult<Entity>.Refused("The entity could not be copied.");
		}

		var duplicatedSnapshots = CloneForDuplication(snapshots);
		_sceneSnapshotService.RestoreDeletedEntities(scene, duplicatedSnapshots);
		if (scene.TryGetEntity(duplicatedSnapshots[0].Entity.EntityId, out var duplicatedRoot) == false)
		{
			return SceneAuthoringResult<Entity>.Refused("The copied entity could not be restored.");
		}

		if (IsAuthoring(scene))
		{
			_undoRedoService.CommitCapture(new EntityCreationUndoRedoEntry("Duplicate Entity", duplicatedSnapshots));
			_interactionState.MarkSceneDirty(scene.World);
		}

		return SceneAuthoringResult<Entity>.Success(duplicatedRoot);
	}

	public SceneAuthoringResult DeleteEntities(EditorScene scene, IReadOnlyList<Entity> entities)
	{
		ArgumentNullException.ThrowIfNull(scene);
		ArgumentNullException.ThrowIfNull(entities);

		var world = scene.World;
		var roots = new List<Entity>(entities.Count);
		for (var i = 0; i < entities.Count; i++)
		{
			var entity = entities[i];
			if (world.IsAlive(entity) == false || roots.Contains(entity))
			{
				continue;
			}

			roots.Add(entity);
		}

		// An entity inside another deleted entity's subtree goes with that ancestor, so a prefab root
		// deleted together with its own children is not refused for those children.
		for (var i = roots.Count - 1; i >= 0; i--)
		{
			if (HasAncestorIn(world, roots[i], roots))
			{
				roots.RemoveAt(i);
			}
		}

		for (var i = 0; i < roots.Count; i++)
		{
			if (EditorPrefabUtility.IsNestedPrefabEntity(scene, roots[i]))
			{
				return SceneAuthoringResult.Refused(
					"Cannot delete entities inside prefab instances. Delete the prefab root instance instead.");
			}
		}

		if (roots.Count == 0)
		{
			return SceneAuthoringResult.Refused("None of the entities exist any more.");
		}

		var deletedEntities = new List<Entity>();
		for (var i = 0; i < roots.Count; i++)
		{
			CollectEntitySubtree(roots[i], world, deletedEntities);
		}

		var snapshots = IsAuthoring(scene)
			? _sceneSnapshotService.CaptureDeletedEntities(scene, deletedEntities)
			: [];

		for (var i = 0; i < roots.Count; i++)
		{
			world.DestroyEntity(roots[i]);
		}

		for (var i = 0; i < deletedEntities.Count; i++)
		{
			var deletedEntity = deletedEntities[i];
			scene.EntityIcons.Remove(deletedEntity);
			scene.EntityCellKeys.Remove(deletedEntity);
			scene.EntityIds.Remove(deletedEntity);
			scene.EntityPrefabSourcePaths.Remove(deletedEntity);
		}

		RemoveDeletedEntitiesFromSelection(world, deletedEntities);

		if (snapshots.Count > 0)
		{
			_undoRedoService.CommitCapture(new EntityDeletionUndoRedoEntry(
				deletedEntities.Count == 1 ? "Delete Entity" : "Delete Entities",
				snapshots));
		}

		_interactionState.MarkSceneDirty(world);

		return SceneAuthoringResult.Success();
	}

	public SceneAuthoringResult<InstantiatedEntities> Instantiate(EditorScene scene, Guid assetId, Vector3? spawnPosition)
	{
		ArgumentNullException.ThrowIfNull(scene);

		var catalog = _projectService.CurrentAssetCatalog;
		if (catalog.TryGetAsset(assetId, out var mountedAsset) == false)
		{
			return SceneAuthoringResult<InstantiatedEntities>.Refused($"Asset '{assetId:D}' is not in the project.");
		}

		var assetType = mountedAsset.Asset.Type;
		if (assetType != AssetType.Model3D &&
		    assetType != AssetType.Prefab)
		{
			return SceneAuthoringResult<InstantiatedEntities>.Refused(
				$"'{mountedAsset.Asset.Name}' is a {assetType} asset; only 3D models and prefabs can be placed in a scene.");
		}

		var world = scene.World;
		var existingEntities = new HashSet<Entity>(EnumerateTransformEntities(world));
		if (assetType == AssetType.Model3D)
		{
			_assetPipelineService.InstantiateImportedModel(catalog, assetId, world, spawnPosition);
		}
		else
		{
			_assetPipelineService.InstantiatePrefab(catalog, assetId, scene, spawnPosition);
		}

		var createdEntities = new List<Entity>();
		foreach (var entity in EnumerateTransformEntities(world))
		{
			if (existingEntities.Contains(entity) == false)
			{
				createdEntities.Add(entity);
			}
		}

		if (createdEntities.Count == 0)
		{
			return SceneAuthoringResult<InstantiatedEntities>.Refused($"'{mountedAsset.Asset.Name}' has nothing to place.");
		}

		var root = createdEntities[0];
		for (var i = 0; i < createdEntities.Count; i++)
		{
			if (world.HasComponent<Parent>(createdEntities[i]) == false)
			{
				root = createdEntities[i];
				break;
			}
		}

		var orderedEntities = new List<Entity>(createdEntities.Count);
		CollectEntitySubtree(root, world, orderedEntities);
		for (var i = 0; i < createdEntities.Count; i++)
		{
			if (orderedEntities.Contains(createdEntities[i]) == false)
			{
				orderedEntities.Add(createdEntities[i]);
			}
		}

		for (var i = 0; i < orderedEntities.Count; i++)
		{
			_sceneSnapshotService.EnsurePersistentEntityId(scene, orderedEntities[i]);
		}

		RecordCreation(scene, $"Add {mountedAsset.Asset.Name}", orderedEntities);

		return SceneAuthoringResult<InstantiatedEntities>.Success(new InstantiatedEntities(root, orderedEntities));
	}

	public SceneAuthoringResult<IReadOnlyList<Entity>> ReparentEntities(
		EditorScene scene,
		IReadOnlyList<Entity> entities,
		Entity? parent)
	{
		ArgumentNullException.ThrowIfNull(scene);
		ArgumentNullException.ThrowIfNull(entities);

		var world = scene.World;
		if (parent is { } parentEntity &&
		    (world.IsAlive(parentEntity) == false || EditorPrefabUtility.IsPrefabEntity(scene, parentEntity)))
		{
			return SceneAuthoringResult<IReadOnlyList<Entity>>.Refused(
				"Entities cannot be parented to a prefab instance or a deleted entity.");
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
				return SceneAuthoringResult<IReadOnlyList<Entity>>.Refused(
					"An entity cannot be parented to itself or to one of its own children.");
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
			return SceneAuthoringResult<IReadOnlyList<Entity>>.Refused("No entity needed to move.");
		}

		var before = new EntityHierarchySnapshot[movedEntities.Count];
		var worldTransforms = new Matrix4x4?[movedEntities.Count];
		for (var i = 0; i < movedEntities.Count; i++)
		{
			var entity = movedEntities[i];
			before[i] = CaptureHierarchySnapshot(scene, entity);
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

			after[i] = CaptureHierarchySnapshot(scene, entity);
		}

		if (IsAuthoring(scene))
		{
			var description = (parent is null ? "Unparent " : "Reparent ") +
			                  (movedEntities.Count == 1 ? "Entity" : "Entities");
			_undoRedoService.CommitCapture(new EntityHierarchyUndoRedoEntry(description, before, after));
			_interactionState.MarkSceneDirty(world);
		}

		return SceneAuthoringResult<IReadOnlyList<Entity>>.Success(movedEntities);
	}

	public ISceneTransformEdit BeginTransformEdit(EditorScene scene, IReadOnlyList<Entity> entities)
	{
		ArgumentNullException.ThrowIfNull(scene);
		ArgumentNullException.ThrowIfNull(entities);

		return new TransformEdit(this, scene, entities);
	}

	public ISceneComponentEdit BeginComponentEdit(
		EditorScene scene,
		IReadOnlyList<Entity> entities,
		Type componentType,
		string description)
	{
		ArgumentNullException.ThrowIfNull(scene);
		ArgumentNullException.ThrowIfNull(entities);
		ArgumentNullException.ThrowIfNull(componentType);

		if (componentType == typeof(LocalTransform))
		{
			return new TransformEdit(this, scene, entities, description);
		}

		return new ComponentEdit(this, scene, entities, componentType, description);
	}

	public SceneAuthoringResult AddComponent(EditorScene scene, Entity entity, Type componentType)
	{
		ArgumentNullException.ThrowIfNull(scene);
		ArgumentNullException.ThrowIfNull(componentType);

		var world = scene.World;
		if (world.IsAlive(entity) == false)
		{
			return SceneAuthoringResult.Refused("The entity no longer exists.");
		}

		if (world.HasComponent(entity, componentType))
		{
			return SceneAuthoringResult.Refused($"The entity already has a {componentType.Name}.");
		}

		var iconBefore = scene.EntityIcons.TryGetValue(entity, out var existingIcon)
			? existingIcon
			: null;
		RuntimeComponentAccessor.AddDefault(world, entity, componentType);
		var defaultIcon = componentType.GetCustomAttribute<DefaultIconAttribute>()?.iconName;
		if (defaultIcon is not null)
		{
			scene.EntityIcons.TryAdd(entity, defaultIcon);
		}

		if (IsAuthoring(scene))
		{
			var description = $"Add {componentType.Name}";
			var snapshot = _sceneSnapshotService.CaptureComponent(scene, entity, componentType);
			var addition = new SceneComponentAdditionUndoRedoEntry(description, [snapshot]);
			var iconAfter = scene.EntityIcons.TryGetValue(entity, out var currentIcon)
				? currentIcon
				: null;
			IEditorUndoRedoEntry entry = string.Equals(iconBefore, iconAfter, StringComparison.Ordinal)
				? addition
				: new CompositeUndoRedoEntry(
					description,
					[
						addition,
						new EntityIconUndoRedoEntry(description, snapshot.EntityId, iconBefore, iconAfter)
					]);
			_undoRedoService.CommitCapture(entry);
			_interactionState.MarkSceneDirty(world);
		}

		return SceneAuthoringResult.Success();
	}

	public SceneAuthoringResult RemoveComponent(EditorScene scene, Entity entity, Type componentType)
	{
		ArgumentNullException.ThrowIfNull(scene);
		ArgumentNullException.ThrowIfNull(componentType);

		var world = scene.World;
		if (world.IsAlive(entity) == false || world.HasComponent(entity, componentType) == false)
		{
			return SceneAuthoringResult.Refused($"The entity has no {componentType.Name} to remove.");
		}

		var snapshot = IsAuthoring(scene)
			? _sceneSnapshotService.CaptureComponent(scene, entity, componentType)
			: (SceneComponentSnapshot?)null;
		RuntimeComponentAccessor.Remove(world, entity, componentType);

		if (snapshot is { } removed)
		{
			_undoRedoService.CommitCapture(new SceneComponentRemovalUndoRedoEntry($"Remove {componentType.Name}", [removed]));
			_interactionState.MarkSceneDirty(world);
		}

		return SceneAuthoringResult.Success();
	}

	public SceneAuthoringResult SetEnabled(EditorScene scene, Entity entity, bool enabled)
	{
		ArgumentNullException.ThrowIfNull(scene);

		var world = scene.World;
		if (world.IsAlive(entity) == false)
		{
			return SceneAuthoringResult.Refused("The entity no longer exists.");
		}

		var wasEnabled = world.IsEnabled(entity);
		if (wasEnabled == enabled)
		{
			return SceneAuthoringResult.Refused(enabled ? "The entity is already enabled." : "The entity is already disabled.");
		}

		world.SetEnabled(entity, enabled);

		if (IsAuthoring(scene))
		{
			var entityId = _sceneSnapshotService.EnsurePersistentEntityId(scene, entity);
			_undoRedoService.CommitCapture(new EntityEnabledUndoRedoEntry(
				enabled ? "Enable Entity" : "Disable Entity",
				entityId,
				wasEnabled,
				enabled));
			_interactionState.MarkSceneDirty(world);
		}

		return SceneAuthoringResult.Success();
	}

	public SceneAuthoringResult SetIcon(EditorScene scene, Entity entity, string iconName)
	{
		ArgumentNullException.ThrowIfNull(scene);
		ArgumentException.ThrowIfNullOrWhiteSpace(iconName);

		if (scene.World.IsAlive(entity) == false)
		{
			return SceneAuthoringResult.Refused("The entity no longer exists.");
		}

		var iconBefore = scene.EntityIcons.TryGetValue(entity, out var existingIcon)
			? existingIcon
			: null;
		if (string.Equals(iconBefore, iconName, StringComparison.Ordinal))
		{
			return SceneAuthoringResult.Refused("The entity already uses that icon.");
		}

		scene.EntityIcons[entity] = iconName;

		if (IsAuthoring(scene))
		{
			var entityId = _sceneSnapshotService.EnsurePersistentEntityId(scene, entity);
			_undoRedoService.CommitCapture(new EntityIconUndoRedoEntry("Change Icon", entityId, iconBefore, iconName));
			_interactionState.MarkSceneDirty(scene.World);
		}

		return SceneAuthoringResult.Success();
	}

	/// <summary>Puts an entity back under the parent and at the local transform a hierarchy snapshot recorded.</summary>
	internal static void ApplyHierarchySnapshot(EditorScene scene, EntityHierarchySnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(scene);

		var world = scene.World;
		if (scene.TryGetEntity(snapshot.EntityId, out var entity) == false)
		{
			return;
		}

		if (snapshot.ParentEntityId is { } parentEntityId && scene.TryGetEntity(parentEntityId, out var parent))
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

	private static bool IsAuthoring(EditorScene scene) => scene.World.Tag == WorldTag.Authoring;

	private void RecordCreation(EditorScene scene, string description, IReadOnlyList<Entity> createdEntities)
	{
		if (IsAuthoring(scene) == false)
		{
			return;
		}

		var snapshots = _sceneSnapshotService.CaptureDeletedEntities(scene, createdEntities);
		if (snapshots.Count > 0)
		{
			_undoRedoService.CommitCapture(new EntityCreationUndoRedoEntry(description, snapshots));
		}

		_interactionState.MarkSceneDirty(scene.World);
	}

	private static void RemoveDeletedEntitiesFromSelection(World world, IReadOnlyList<Entity> deletedEntities)
	{
		var selectionChanged = false;
		for (var i = 0; i < deletedEntities.Count; i++)
		{
			if (EditorGui.SelectedEntities.Contains(deletedEntities[i]))
			{
				selectionChanged = true;
				break;
			}
		}

		if (selectionChanged)
		{
			EditorGui.RefreshSelectedEntity(world);
		}
	}

	private static void AddLight(EditorScene scene, Entity entity, Light light)
	{
		scene.World.AddComponent(entity, light);
		scene.EntityIcons[entity] = "light";
	}

	private static void AddMeshRenderer(World world, Entity entity, AssetRef<Mesh> mesh)
	{
		// Only the references are stored; the renderer resolves the assets when it validates the component.
		world.AddComponent(entity, new MeshRenderer
		{
			MeshAsset = mesh,
			MaterialAsset = BuiltInEngineAssets.DefaultMaterial
		});
	}

	private static List<Entity> EnumerateTransformEntities(World world)
	{
		var entities = new List<Entity>();
		foreach (var entry in world.View<LocalTransform>())
		{
			entities.Add(entry.Entity);
		}

		return entities;
	}

	private EntityHierarchySnapshot CaptureHierarchySnapshot(EditorScene scene, Entity entity)
	{
		var world = scene.World;
		var parentEntityId = world.HasComponent<Parent>(entity)
			? _sceneSnapshotService.EnsurePersistentEntityId(scene, world.GetComponent<Parent>(entity).Value)
			: (Guid?)null;
		var localTransform = world.HasComponent<LocalTransform>(entity)
			? world.GetComponent<LocalTransform>(entity).GetTransform()
			: (Matrix4x4?)null;

		return new EntityHierarchySnapshot(
			_sceneSnapshotService.EnsurePersistentEntityId(scene, entity),
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

	private class ComponentEdit : ISceneComponentEdit
	{
		private readonly SceneAuthoringService _owner;
		private readonly string _description;
		private readonly List<Entity> _entities;
		private readonly object[] _startValues;
		private readonly SceneComponentSnapshot[] _beforeSnapshots;
		private bool _ended;

		public ComponentEdit(
			SceneAuthoringService owner,
			EditorScene scene,
			IReadOnlyList<Entity> entities,
			Type componentType,
			string description)
		{
			_owner = owner;
			Scene = scene;
			ComponentType = componentType;
			_description = string.IsNullOrWhiteSpace(description)
				? $"Edit {componentType.Name}"
				: description;

			var world = scene.World;
			_entities = new List<Entity>(entities.Count);
			for (var i = 0; i < entities.Count; i++)
			{
				var entity = entities[i];
				if (world.IsAlive(entity) &&
				    world.HasComponent(entity, componentType) &&
				    _entities.Contains(entity) == false)
				{
					_entities.Add(entity);
				}
			}

			_startValues = new object[_entities.Count];
			for (var i = 0; i < _entities.Count; i++)
			{
				_startValues[i] = CaptureValue(_entities[i]);
			}

			_beforeSnapshots = IsAuthoring(scene)
				? CaptureSnapshots()
				: [];
		}

		protected EditorScene Scene { get; }

		public IReadOnlyList<Entity> Entities => _entities;

		public Type ComponentType { get; }

		public void Apply(Action<Entity> change)
		{
			ArgumentNullException.ThrowIfNull(change);
			ThrowIfEnded();

			var world = Scene.World;
			for (var i = 0; i < _entities.Count; i++)
			{
				var entity = _entities[i];
				if (world.IsAlive(entity) && world.HasComponent(entity, ComponentType))
				{
					change(entity);
				}
			}
		}

		public bool Commit()
		{
			End();

			var world = Scene.World;
			var changed = false;
			for (var i = 0; i < _entities.Count; i++)
			{
				if (IsStillEditable(_entities[i]) && ValueEquals(_startValues[i], CaptureValue(_entities[i])) == false)
				{
					changed = true;
					break;
				}
			}

			if (changed == false || IsAuthoring(Scene) == false)
			{
				return changed;
			}

			var changedBefore = new List<SceneComponentSnapshot>(_entities.Count);
			var changedAfter = new List<SceneComponentSnapshot>(_entities.Count);
			for (var i = 0; i < _entities.Count; i++)
			{
				if (IsStillEditable(_entities[i]) == false)
				{
					continue;
				}

				var after = _owner._sceneSnapshotService.CaptureComponent(Scene, _entities[i], ComponentType);
				if (SnapshotDataEquals(_beforeSnapshots[i], after))
				{
					continue;
				}

				changedBefore.Add(_beforeSnapshots[i]);
				changedAfter.Add(after);
			}

			if (changedBefore.Count > 0)
			{
				_owner._undoRedoService.CommitCapture(new SceneComponentEditUndoRedoEntry(_description, changedBefore, changedAfter));
			}

			_owner._interactionState.MarkSceneDirty(world);

			return true;
		}

		public void Cancel()
		{
			End();

			for (var i = 0; i < _entities.Count; i++)
			{
				if (IsStillEditable(_entities[i]))
				{
					RestoreValue(_entities[i], _startValues[i]);
				}
			}
		}

		protected bool CanEdit(Entity entity)
		{
			ThrowIfEnded();

			return _entities.Contains(entity) && IsStillEditable(entity);
		}

		protected virtual object CaptureValue(Entity entity)
		{
			return RuntimeComponentAccessor.ReadBoxed(Scene.World, entity, ComponentType);
		}

		protected virtual void RestoreValue(Entity entity, object value)
		{
			RuntimeComponentAccessor.WriteBoxed(Scene.World, entity, ComponentType, value);
		}

		protected virtual bool ValueEquals(object start, object current)
		{
			return string.Equals(
				JsonSerializer.Serialize(start, ComponentType, AssetJson.SerializerOptions),
				JsonSerializer.Serialize(current, ComponentType, AssetJson.SerializerOptions),
				StringComparison.Ordinal);
		}

		private bool IsStillEditable(Entity entity)
		{
			return Scene.World.IsAlive(entity) && Scene.World.HasComponent(entity, ComponentType);
		}

		private SceneComponentSnapshot[] CaptureSnapshots()
		{
			var snapshots = new SceneComponentSnapshot[_entities.Count];
			for (var i = 0; i < _entities.Count; i++)
			{
				snapshots[i] = _owner._sceneSnapshotService.CaptureComponent(Scene, _entities[i], ComponentType);
			}

			return snapshots;
		}

		private static bool SnapshotDataEquals(SceneComponentSnapshot left, SceneComponentSnapshot right)
		{
			return left.EntityId == right.EntityId &&
			       string.Equals(
				       JsonSerializer.Serialize(left.Data, AssetJson.SerializerOptions),
				       JsonSerializer.Serialize(right.Data, AssetJson.SerializerOptions),
				       StringComparison.Ordinal);
		}

		private void ThrowIfEnded()
		{
			if (_ended)
			{
				throw new InvalidOperationException("This component edit has already ended.");
			}
		}

		private void End()
		{
			ThrowIfEnded();
			_ended = true;
		}
	}

	private sealed class TransformEdit : ComponentEdit, ISceneTransformEdit
	{
		public TransformEdit(
			SceneAuthoringService owner,
			EditorScene scene,
			IReadOnlyList<Entity> entities,
			string description = "Transform")
			: base(owner, scene, entities, typeof(LocalTransform), description)
		{
		}

		public void SetWorldPosition(Entity entity, Vector3 worldPosition)
		{
			if (CanEdit(entity))
			{
				Scene.World.SetWorldPosition(entity, worldPosition);
			}
		}

		public void SetWorldRotation(Entity entity, Quaternion worldRotation)
		{
			if (CanEdit(entity))
			{
				Scene.World.SetWorldRotation(entity, worldRotation);
			}
		}

		public void SetLocalRotation(Entity entity, Quaternion localRotation)
		{
			if (CanEdit(entity))
			{
				Scene.World.SetLocalRotation(entity, localRotation);
			}
		}

		public void SetLocalScale(Entity entity, Vector3 localScale)
		{
			if (CanEdit(entity))
			{
				Scene.World.SetLocalScale(entity, localScale);
			}
		}

		protected override object CaptureValue(Entity entity)
		{
			return Scene.World.GetComponent<LocalTransform>(entity).GetTransform();
		}

		protected override void RestoreValue(Entity entity, object value)
		{
			ApplyLocalTransform(Scene.World, entity, (Matrix4x4)value);
		}

		protected override bool ValueEquals(object start, object current)
		{
			return (Matrix4x4)start == (Matrix4x4)current;
		}
	}
}
