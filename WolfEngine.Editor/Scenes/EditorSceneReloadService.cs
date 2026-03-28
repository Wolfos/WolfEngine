using System.Text.Json;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
using WolfEngine.Mathematics;

namespace WolfEngine.Editor;

public sealed class EditorSceneReloadSnapshot
{
	public required Guid AssetId { get; init; }
	public required string Name { get; init; }
	public required string RelativeAssetPath { get; init; }
	public required Cell GlobalCell { get; init; }
	public required Dictionary<Int2, Cell> SpatialCells { get; init; }
}

public interface IEditorSceneReloadService
{
	EditorSceneReloadSnapshot Capture(EditorScene scene);
	EditorScene Restore(EditorSceneReloadSnapshot snapshot, WorldTag worldTag = WorldTag.Authoring);
}

public sealed class EditorSceneReloadService : IEditorSceneReloadService
{
	private readonly IProjectTypeResolver _typeResolver;

	public EditorSceneReloadService(IProjectTypeResolver typeResolver)
	{
		_typeResolver = typeResolver ?? throw new ArgumentNullException(nameof(typeResolver));
	}

	public EditorSceneReloadSnapshot Capture(EditorScene scene)
	{
		ArgumentNullException.ThrowIfNull(scene);
		ArgumentNullException.ThrowIfNull(scene.World);

		var serializedGlobalCell = new Cell
		{
			RelativePath = scene.GlobalCell.RelativePath,
			Entities = []
		};
		var serializedSpatialCells = scene.SpatialCells.ToDictionary(
			entry => entry.Key,
			entry => new Cell
			{
				RelativePath = entry.Value.RelativePath,
				Entities = []
			});

		var entities = new List<Entity>();
		scene.World.GetAllEntities(entities);
		for (var i = 0; i < entities.Count; i++)
		{
			var entity = entities[i];
			if (scene.EntityIds.TryGetValue(entity, out var entityId) && entityId != Guid.Empty)
			{
				continue;
			}

			scene.EntityIds[entity] = Guid.NewGuid();
		}

		for (var i = 0; i < entities.Count; i++)
		{
			var entity = entities[i];
			var cellKey = scene.EntityCellKeys.TryGetValue(entity, out var storedCellKey)
				? storedCellKey
				: SceneCellKey.Global;
			if (cellKey.IsGlobal == false && serializedSpatialCells.ContainsKey(cellKey.Coordinates) == false)
			{
				cellKey = SceneCellKey.Global;
			}

			var serializedEntity = SerializeEntity(scene, entity, scene.EntityIds[entity]);
			if (cellKey.IsGlobal)
			{
				serializedGlobalCell.Entities.Add(serializedEntity);
			}
			else
			{
				serializedSpatialCells[cellKey.Coordinates].Entities.Add(serializedEntity);
			}
		}

		return new EditorSceneReloadSnapshot
		{
			AssetId = scene.AssetId,
			Name = scene.Name,
			RelativeAssetPath = scene.RelativeAssetPath,
			GlobalCell = serializedGlobalCell,
			SpatialCells = serializedSpatialCells
		};
	}

	public EditorScene Restore(EditorSceneReloadSnapshot snapshot, WorldTag worldTag = WorldTag.Authoring)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		var scene = new EditorScene
		{
			AssetId = snapshot.AssetId,
			Name = snapshot.Name,
			RelativeAssetPath = snapshot.RelativeAssetPath,
			World = new World(worldTag),
			EntityIcons = new Dictionary<Entity, string>(),
			GlobalCell = CloneCell(snapshot.GlobalCell),
			SpatialCells = snapshot.SpatialCells.ToDictionary(entry => entry.Key, entry => CloneCell(entry.Value)),
			EntityCellKeys = new Dictionary<Entity, SceneCellKey>(),
			EntityIds = new Dictionary<Entity, Guid>()
		};

		var loadedCells = new List<(SceneCellKey CellKey, Cell Cell)>
		{
			(SceneCellKey.Global, scene.GlobalCell)
		};
		foreach (var spatialCell in scene.SpatialCells)
		{
			loadedCells.Add((SceneCellKey.Spatial(spatialCell.Key), spatialCell.Value));
		}

		var entitiesById = CreateEntities(scene, loadedCells);
		ApplyEntityState(scene.World, loadedCells, entitiesById);
		RestoreHierarchy(scene.World, loadedCells, entitiesById);
		return scene;
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
			Name = hasName
				? world.GetComponent<NameComponent>(entity).Name ?? string.Empty
				: string.Empty,
			Enabled = world.IsEnabled(entity),
			Icon = scene.EntityIcons.TryGetValue(entity, out var iconName) ? iconName : string.Empty,
			LocalTransform = world.HasComponent<LocalTransform>(entity)
				? world.GetComponent<LocalTransform>(entity).GetTransform()
				: null,
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
					Data = JsonSerializer.SerializeToElement(global::WolfEngine.Editor.UI.RuntimeComponentAccessor.ReadBoxed(world, entity, componentType), componentType, AssetJson.GetSerializerOptions(componentType))
			});
		}

		return savedEntity;
	}

	private void ApplyEntityState(World world, List<(SceneCellKey CellKey, Cell Cell)> loadedCells, Dictionary<Guid, Entity> entitiesById)
	{
		for (var i = 0; i < loadedCells.Count; i++)
		{
			var cell = loadedCells[i].Cell;
			for (var entityIndex = 0; entityIndex < cell.Entities.Count; entityIndex++)
			{
				var savedEntity = cell.Entities[entityIndex];
				var entity = entitiesById[savedEntity.EntityId];
				world.SetEnabled(entity, savedEntity.Enabled);
				for (var componentIndex = 0; componentIndex < savedEntity.Components.Count; componentIndex++)
				{
					ApplyComponent(world, entity, savedEntity.Components[componentIndex]);
				}
			}
		}
	}

	private void ApplyComponent(World world, Entity entity, SavedComponent component)
	{
		if (TryResolveComponentType(component, out var componentType) == false || IsPersistableComponentType(componentType) == false)
		{
			return;
		}

		var deserialized = ProjectTypeStateTransferUtility.DeserializeWithFieldMerge(component.Data, componentType);
		global::WolfEngine.Editor.UI.RuntimeComponentAccessor.WriteBoxed(world, entity, componentType, deserialized);
	}

	private bool TryResolveComponentType(SavedComponent component, out Type componentType)
	{
		if (_typeResolver.TryResolveStableTypeId(component.TypeId, out componentType))
		{
			return true;
		}

		if (_typeResolver.TryResolveType(component.Type, out componentType))
		{
			return true;
		}

		return ProjectTypeResolverUtility.TryResolveFromLoadedAssemblies(component.Type, out componentType);
	}

	private static Dictionary<Guid, Entity> CreateEntities(EditorScene scene, List<(SceneCellKey CellKey, Cell Cell)> loadedCells)
	{
		var entitiesById = new Dictionary<Guid, Entity>();
		for (var i = 0; i < loadedCells.Count; i++)
		{
			var (cellKey, cell) = loadedCells[i];
			for (var entityIndex = 0; entityIndex < cell.Entities.Count; entityIndex++)
			{
				var savedEntity = cell.Entities[entityIndex];
				var entity = CreateEntity(scene.World, savedEntity);
				entitiesById[savedEntity.EntityId] = entity;
				scene.EntityIds[entity] = savedEntity.EntityId;
				scene.EntityCellKeys[entity] = cellKey;
				if (string.IsNullOrWhiteSpace(savedEntity.Icon) == false)
				{
					scene.EntityIcons[entity] = savedEntity.Icon;
				}
			}
		}

		return entitiesById;
	}

	private static void RestoreHierarchy(World world, List<(SceneCellKey CellKey, Cell Cell)> loadedCells, Dictionary<Guid, Entity> entitiesById)
	{
		for (var i = 0; i < loadedCells.Count; i++)
		{
			var cell = loadedCells[i].Cell;
			for (var entityIndex = 0; entityIndex < cell.Entities.Count; entityIndex++)
			{
				var savedEntity = cell.Entities[entityIndex];
				if (savedEntity.ParentEntityId is not { } parentEntityId || entitiesById.TryGetValue(parentEntityId, out var parent) == false)
				{
					continue;
				}

				world.SetParent(entitiesById[savedEntity.EntityId], parent);
			}
		}
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

	private static Cell CloneCell(Cell source)
	{
		return new Cell
		{
			RelativePath = source.RelativePath,
			Entities = source.Entities.Select(CloneEntity).ToList()
		};
	}

	private static SavedEntity CloneEntity(SavedEntity source)
	{
		return new SavedEntity
		{
			EntityId = source.EntityId,
			ParentEntityId = source.ParentEntityId,
			HasName = source.HasName,
			Name = source.Name,
			Enabled = source.Enabled,
			Icon = source.Icon,
			LocalTransform = source.LocalTransform,
			Components = source.Components.Select(component => new SavedComponent
			{
				Type = component.Type,
				TypeId = component.TypeId,
				Data = component.Data.Clone()
			}).ToList()
		};
	}
}
