using System.Text.Json;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;

namespace WolfEngine.Editor;

public interface IEditorSceneFactory
{
	public EditorScene New();
	public EditorScene Load(Guid node);
	public void Save(EditorScene scene);
}

public class EditorSceneFactory : IEditorSceneFactory
{
	private readonly IEditorProjectService _projectService;
	private readonly IProjectAssetPipelineService _assetPipelineService;
	private readonly IProjectTypeResolver? _typeResolver;

	public EditorSceneFactory(
		IEditorProjectService projectService,
		IProjectAssetPipelineService assetPipelineService,
		IProjectTypeResolver? typeResolver = null)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_assetPipelineService = assetPipelineService ?? throw new ArgumentNullException(nameof(assetPipelineService));
		_typeResolver = typeResolver;
	}

	public EditorScene New()
	{
		return new EditorScene
		{
			Name = "Untitled Scene",
			World = new World(WorldTag.Game),
			EntityIcons = new Dictionary<Entity, string>(),
			GlobalCell = new Cell(),
			SpatialCells = new Dictionary<Int2, Cell>(),
			EntityCellKeys = new Dictionary<Entity, SceneCellKey>(),
			EntityIds = new Dictionary<Entity, Guid>()
		};
	}

	public EditorScene Load(Guid node)
	{
		EnsureProjectOpen();
		if (_projectService.TryGetAsset(node, out var asset) == false)
		{
			throw new InvalidOperationException($"Scene asset '{node}' was not found in the current project.");
		}

		if (asset.Type != AssetType.Scene)
		{
			throw new InvalidOperationException($"Asset '{node}' is '{asset.Type}', not a scene.");
		}

		var sceneAsset = EditorSceneAssetFile.Load(_projectService.GetAbsolutePath(asset.RelativeAssetPath));
		var scene = new EditorScene
		{
			AssetId = node,
			Name = string.IsNullOrWhiteSpace(sceneAsset.Name) ? asset.Name : sceneAsset.Name,
			RelativeAssetPath = asset.RelativeAssetPath,
			World = new World(WorldTag.Game),
			EntityIcons = new Dictionary<Entity, string>(),
			GlobalCell = LoadCell(sceneAsset.GlobalCellPath),
			SpatialCells = new Dictionary<Int2, Cell>(),
			EntityCellKeys = new Dictionary<Entity, SceneCellKey>(),
			EntityIds = new Dictionary<Entity, Guid>()
		};

		var loadedCells = new List<(SceneCellKey CellKey, Cell Cell)>
		{
			(SceneCellKey.Global, scene.GlobalCell)
		};
		for (var i = 0; i < sceneAsset.SpatialCells.Count; i++)
		{
			var spatialCellEntry = sceneAsset.SpatialCells[i];
			var coordinates = spatialCellEntry.ToCoordinates();
			var cell = LoadCell(spatialCellEntry.Path);
			scene.SpatialCells[coordinates] = cell;
			loadedCells.Add((SceneCellKey.Spatial(coordinates), cell));
		}

		var entitiesById = CreateEntities(scene, loadedCells);
		ApplyEntityState(scene, loadedCells, entitiesById);
		RestoreHierarchy(scene.World, loadedCells, entitiesById);
		return scene;
	}

	public void Save(EditorScene scene)
	{
		EnsureProjectOpen();
		ArgumentNullException.ThrowIfNull(scene);
		ArgumentNullException.ThrowIfNull(scene.World);

		if (string.IsNullOrWhiteSpace(scene.Name))
		{
			scene.Name = "Untitled Scene";
		}

		var relativeScenePath = string.IsNullOrWhiteSpace(scene.RelativeAssetPath)
			? GetDefaultSceneAssetPath(scene.Name)
			: NormalizeRelativePath(scene.RelativeAssetPath);
		var absoluteScenePath = _projectService.GetAbsolutePath(relativeScenePath);
		var sceneFolderRelativePath = NormalizeRelativePath(Path.GetDirectoryName(relativeScenePath) ?? Path.Combine("Assets", "Scenes"));

		var previousManifest = File.Exists(absoluteScenePath)
			? EditorSceneAssetFile.Load(absoluteScenePath)
			: null;

		var serializedGlobalCell = new Cell
		{
			RelativePath = string.IsNullOrWhiteSpace(scene.GlobalCell.RelativePath)
				? NormalizeRelativePath(Path.Combine(sceneFolderRelativePath, $"global{Cell.FileExtension}"))
				: NormalizeRelativePath(scene.GlobalCell.RelativePath),
			Entities = []
		};
		var serializedSpatialCells = scene.SpatialCells.ToDictionary(
			entry => entry.Key,
			entry => new Cell
			{
				RelativePath = string.IsNullOrWhiteSpace(entry.Value.RelativePath)
					? GetDefaultSpatialCellPath(sceneFolderRelativePath, entry.Key)
					: NormalizeRelativePath(entry.Value.RelativePath),
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
				scene.EntityCellKeys[entity] = cellKey;
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

		scene.GlobalCell = serializedGlobalCell;
		scene.SpatialCells = serializedSpatialCells;

		var manifest = new EditorSceneAssetFile
		{
			Name = scene.Name,
			GlobalCellPath = serializedGlobalCell.RelativePath,
			SpatialCells = serializedSpatialCells
				.OrderBy(entry => entry.Key.X)
				.ThenBy(entry => entry.Key.Y)
				.Select(entry => new SceneSpatialCellFileEntry
				{
					X = entry.Key.X,
					Y = entry.Key.Y,
					Path = entry.Value.RelativePath
				})
				.ToList()
		};

		WriteJsonAtomicallyIfChanged(_projectService.GetAbsolutePath(serializedGlobalCell.RelativePath), serializedGlobalCell);
		foreach (var spatialCell in serializedSpatialCells.Values)
		{
			WriteJsonAtomicallyIfChanged(_projectService.GetAbsolutePath(spatialCell.RelativePath), spatialCell);
		}

		WriteJsonAtomicallyIfChanged(absoluteScenePath, manifest);
		DeleteStaleCellFiles(previousManifest, manifest);

		_projectService.RefreshAssetSource(relativeScenePath);
		scene.RelativeAssetPath = relativeScenePath;
		if (_assetPipelineService.TryGetPrimaryNodeIdForRelativeSourcePath(_projectService.ProjectRootPath!, relativeScenePath, out var nodeId))
		{
			scene.AssetId = nodeId;
		}
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
				if (savedEntity.EntityId == Guid.Empty)
				{
					throw new InvalidOperationException("Scene contains an entity with an empty persistent id.");
				}

				if (entitiesById.ContainsKey(savedEntity.EntityId))
				{
					throw new InvalidOperationException($"Scene contains duplicate entity id '{savedEntity.EntityId}'.");
				}

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

	private void ApplyEntityState(EditorScene scene, List<(SceneCellKey CellKey, Cell Cell)> loadedCells, Dictionary<Guid, Entity> entitiesById)
	{
		for (var i = 0; i < loadedCells.Count; i++)
		{
			var cell = loadedCells[i].Cell;
			for (var entityIndex = 0; entityIndex < cell.Entities.Count; entityIndex++)
			{
				var savedEntity = cell.Entities[entityIndex];
				var entity = entitiesById[savedEntity.EntityId];
				scene.World.SetEnabled(entity, savedEntity.Enabled);
				for (var componentIndex = 0; componentIndex < savedEntity.Components.Count; componentIndex++)
				{
					ApplyComponent(scene.World, entity, savedEntity.Components[componentIndex]);
				}
			}
		}
	}

	private static void RestoreHierarchy(World world, List<(SceneCellKey CellKey, Cell Cell)> loadedCells, Dictionary<Guid, Entity> entitiesById)
	{
		for (var i = 0; i < loadedCells.Count; i++)
		{
			var cell = loadedCells[i].Cell;
			for (var entityIndex = 0; entityIndex < cell.Entities.Count; entityIndex++)
			{
				var savedEntity = cell.Entities[entityIndex];
				if (savedEntity.ParentEntityId is not { } parentEntityId)
				{
					continue;
				}

				if (entitiesById.TryGetValue(parentEntityId, out var parent) == false)
				{
					continue;
				}

				world.SetParent(entitiesById[savedEntity.EntityId], parent);
			}
		}
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

			savedEntity.Components.Add(SerializeComponent(world, entity, componentType));
		}

		return savedEntity;
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

	private SavedComponent SerializeComponent(World world, Entity entity, Type componentType)
	{
		return new SavedComponent
		{
			Type = _typeResolver?.GetTypeName(componentType) ?? ProjectTypeResolverUtility.GetTypeName(componentType),
			Data = JsonSerializer.SerializeToElement(RuntimeComponentAccessor.ReadBoxed(world, entity, componentType), componentType, AssetJson.SerializerOptions)
		};
	}

	private void ApplyComponent(World world, Entity entity, SavedComponent component)
	{
		if (TryResolveComponentType(component.Type, out var componentType) == false || IsPersistableComponentType(componentType) == false)
		{
			return;
		}

		var deserialized = component.Data.Deserialize(componentType, AssetJson.SerializerOptions)
			?? throw new InvalidOperationException($"Failed to deserialize scene component '{componentType.FullName}'.");
		RuntimeComponentAccessor.WriteBoxed(world, entity, componentType, deserialized);
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

	private Cell LoadCell(string relativePath)
	{
		if (string.IsNullOrWhiteSpace(relativePath))
		{
			throw new InvalidOperationException("Scene cell path cannot be empty.");
		}

		var absolutePath = _projectService.GetAbsolutePath(relativePath);
		if (File.Exists(absolutePath) == false)
		{
			throw new InvalidOperationException($"Scene cell '{relativePath}' was not found.");
		}

		var json = File.ReadAllText(absolutePath);
		var cell = JsonSerializer.Deserialize<Cell>(json, AssetJson.SerializerOptions)
			?? throw new InvalidOperationException($"Failed to deserialize scene cell '{relativePath}'.");
		if (cell.Version != Cell.CurrentVersion)
		{
			throw new InvalidOperationException($"Unsupported scene cell version {cell.Version}. Expected {Cell.CurrentVersion}.");
		}

		cell.RelativePath = NormalizeRelativePath(relativePath);
		cell.Entities ??= [];
		return cell;
	}

	private void DeleteStaleCellFiles(EditorSceneAssetFile? previousManifest, EditorSceneAssetFile currentManifest)
	{
		if (previousManifest is null)
		{
			return;
		}

		var currentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			NormalizeRelativePath(currentManifest.GlobalCellPath)
		};
		for (var i = 0; i < currentManifest.SpatialCells.Count; i++)
		{
			currentPaths.Add(NormalizeRelativePath(currentManifest.SpatialCells[i].Path));
		}

		var previousPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			NormalizeRelativePath(previousManifest.GlobalCellPath)
		};
		for (var i = 0; i < previousManifest.SpatialCells.Count; i++)
		{
			previousPaths.Add(NormalizeRelativePath(previousManifest.SpatialCells[i].Path));
		}

		foreach (var previousPath in previousPaths)
		{
			if (currentPaths.Contains(previousPath))
			{
				continue;
			}

			var absolutePath = _projectService.GetAbsolutePath(previousPath);
			if (File.Exists(absolutePath))
			{
				File.Delete(absolutePath);
			}
		}
	}

	private static void WriteJsonAtomicallyIfChanged<T>(string absolutePath, T value)
	{
		var directory = Path.GetDirectoryName(absolutePath);
		if (string.IsNullOrWhiteSpace(directory) == false)
		{
			Directory.CreateDirectory(directory);
		}

		var json = JsonSerializer.Serialize(value, AssetJson.SerializerOptions);
		if (File.Exists(absolutePath))
		{
			var existingJson = File.ReadAllText(absolutePath);
			if (string.Equals(existingJson, json, StringComparison.Ordinal))
			{
				return;
			}
		}

		var tempPath = absolutePath + ".tmp";
		File.WriteAllText(tempPath, json);
		File.Move(tempPath, absolutePath, true);
	}

	private void EnsureProjectOpen()
	{
		if (_projectService.HasOpenProject == false || string.IsNullOrWhiteSpace(_projectService.ProjectRootPath))
		{
			throw new InvalidOperationException("Open or create a project before saving or loading scenes.");
		}
	}

	private static string GetDefaultSceneAssetPath(string sceneName)
	{
		var safeSceneName = SanitizePathSegment(sceneName);
		return NormalizeRelativePath(Path.Combine("Assets", "Scenes", safeSceneName, $"{safeSceneName}{EditorSceneAssetFile.FileExtension}"));
	}

	private static string GetDefaultSpatialCellPath(string sceneFolderRelativePath, Int2 coordinates)
	{
		return NormalizeRelativePath(Path.Combine(
			sceneFolderRelativePath,
			"cells",
			$"{coordinates.X}_{coordinates.Y}{Cell.FileExtension}"));
	}

	private static string SanitizePathSegment(string value)
	{
		var input = string.IsNullOrWhiteSpace(value) ? "Untitled Scene" : value.Trim();
		var invalidChars = Path.GetInvalidFileNameChars();
		var chars = input.Select(character => invalidChars.Contains(character) ? '_' : character).ToArray();
		var sanitized = new string(chars).Trim();
		return string.IsNullOrWhiteSpace(sanitized) ? "Untitled Scene" : sanitized;
	}

	private static string NormalizeRelativePath(string relativePath)
	{
		return relativePath.Replace('\\', '/');
	}

	private bool TryResolveComponentType(string typeName, out Type componentType)
	{
		if (_typeResolver?.TryResolveType(typeName, out componentType) == true)
		{
			return true;
		}

		return ProjectTypeResolverUtility.TryResolveFromLoadedAssemblies(typeName, out componentType);
	}
}
