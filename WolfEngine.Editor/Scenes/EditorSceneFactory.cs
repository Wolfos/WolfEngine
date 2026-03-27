using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Editor;

public interface IEditorSceneFactory
{
	public EditorScene New();
	public EditorScene Load(Guid node);
	public void Save(EditorScene scene);
}

public class EditorSceneFactory : IEditorSceneFactory
{
	private static readonly ConcurrentDictionary<Type, Func<World, Entity, object>> GetComponentReaders = new();
	private static readonly ConcurrentDictionary<Type, Action<World, Entity, object>> AddComponentWriters = new();
	private static readonly Dictionary<Type, ISceneComponentAdapter> ComponentAdapters = new()
	{
		[typeof(LocalTransform)] = new LocalTransformSceneComponentAdapter(),
		[typeof(Camera)] = new CameraSceneComponentAdapter(),
		[typeof(MeshRenderer)] = new MeshRendererSceneComponentAdapter(),
		[typeof(WorldSettings)] = new WorldSettingsSceneComponentAdapter()
	};

	private readonly IEditorProjectService _projectService;
	private readonly IProjectAssetPipelineService _assetPipelineService;

	public EditorSceneFactory(IEditorProjectService projectService, IProjectAssetPipelineService assetPipelineService)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_assetPipelineService = assetPipelineService ?? throw new ArgumentNullException(nameof(assetPipelineService));
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

				var entity = scene.World.CreateEntity();
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

	private static void ApplyEntityState(EditorScene scene, List<(SceneCellKey CellKey, Cell Cell)> loadedCells, Dictionary<Guid, Entity> entitiesById)
	{
		for (var i = 0; i < loadedCells.Count; i++)
		{
			var cell = loadedCells[i].Cell;
			for (var entityIndex = 0; entityIndex < cell.Entities.Count; entityIndex++)
			{
				var savedEntity = cell.Entities[entityIndex];
				var entity = entitiesById[savedEntity.EntityId];
				if (savedEntity.HasName)
				{
					scene.World.AddComponent(entity, new NameComponent
					{
						Name = savedEntity.Name
					});
				}

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

	private static SavedEntity SerializeEntity(EditorScene scene, Entity entity, Guid entityId)
	{
		var world = scene.World;
		var savedEntity = new SavedEntity
		{
			EntityId = entityId,
			ParentEntityId = TryGetParentEntityId(scene, entity),
			HasName = world.HasComponent<NameComponent>(entity),
			Name = world.HasComponent<NameComponent>(entity)
				? world.GetComponent<NameComponent>(entity).Name ?? string.Empty
				: string.Empty,
			Enabled = world.IsEnabled(entity),
			Icon = scene.EntityIcons.TryGetValue(entity, out var iconName) ? iconName : string.Empty,
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

	private static SavedComponent SerializeComponent(World world, Entity entity, Type componentType)
	{
		var serializedData = ComponentAdapters.TryGetValue(componentType, out var adapter)
			? adapter.Serialize(world, entity)
			: JsonSerializer.SerializeToElement(GetComponentBoxed(world, entity, componentType), componentType, AssetJson.SerializerOptions);
		return new SavedComponent
		{
			Type = componentType.AssemblyQualifiedName
			       ?? throw new InvalidOperationException($"Component type '{componentType.FullName}' does not have an assembly-qualified name."),
			Data = serializedData
		};
	}

	private static void ApplyComponent(World world, Entity entity, SavedComponent component)
	{
		var componentType = Type.GetType(component.Type, throwOnError: false);
		if (componentType is null || IsPersistableComponentType(componentType) == false)
		{
			return;
		}

		if (ComponentAdapters.TryGetValue(componentType, out var adapter))
		{
			adapter.DeserializeAndApply(world, entity, component.Data);
			return;
		}

		var deserialized = component.Data.Deserialize(componentType, AssetJson.SerializerOptions)
			?? throw new InvalidOperationException($"Failed to deserialize scene component '{componentType.FullName}'.");
		AddComponentBoxed(world, entity, componentType, deserialized);
	}

	private static bool IsPersistableComponentType(Type componentType)
	{
		return componentType == typeof(NameComponent) == false
		       && componentType.IsValueType
		       && typeof(IEntityComponent).IsAssignableFrom(componentType)
		       && Attribute.IsDefined(componentType, typeof(ExcludeFromEditorAttribute)) == false
		       && Attribute.IsDefined(componentType, typeof(EditorOnlyAttribute)) == false;
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

	private static object GetComponentBoxed(World world, Entity entity, Type componentType)
	{
		return GetComponentReaders.GetOrAdd(componentType, CreateComponentReader)(world, entity);
	}

	private static void AddComponentBoxed(World world, Entity entity, Type componentType, object componentValue)
	{
		AddComponentWriters.GetOrAdd(componentType, CreateComponentWriter)(world, entity, componentValue);
	}

	private static Func<World, Entity, object> CreateComponentReader(Type componentType)
	{
		var method = typeof(EditorSceneFactory)
			.GetMethod(nameof(ReadComponentGeneric), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
			.MakeGenericMethod(componentType);
		return (Func<World, Entity, object>)Delegate.CreateDelegate(typeof(Func<World, Entity, object>), method);
	}

	private static Action<World, Entity, object> CreateComponentWriter(Type componentType)
	{
		var method = typeof(EditorSceneFactory)
			.GetMethod(nameof(AddComponentGeneric), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
			.MakeGenericMethod(componentType);
		return (Action<World, Entity, object>)Delegate.CreateDelegate(typeof(Action<World, Entity, object>), method);
	}

	private static object ReadComponentGeneric<T>(World world, Entity entity) where T : struct, IEntityComponent
	{
		return world.GetComponent<T>(entity);
	}

	private static void AddComponentGeneric<T>(World world, Entity entity, object componentValue) where T : struct, IEntityComponent
	{
		world.AddComponent(entity, (T)componentValue);
	}

	private sealed class LocalTransformComponentData
	{
		public Vector3 LocalPosition { get; set; }
		public Quaternion LocalRotation { get; set; }
		public Vector3 LocalScale { get; set; }
	}

	private sealed class CameraComponentData
	{
		public int ScreenWidth { get; set; } = 1;
		public int ScreenHeight { get; set; } = 1;
		public float Fov { get; set; }
		public float NearPlane { get; set; }
		public float FarPlane { get; set; }
		public bool AutoResolution { get; set; }
	}

	private sealed class MeshRendererComponentData
	{
		public Guid MeshAssetId { get; set; }
		public Guid MaterialAssetId { get; set; }
	}

	private sealed class WorldSettingsComponentData
	{
		public Guid RenderConfigAssetId { get; set; }
	}

	private interface ISceneComponentAdapter
	{
		JsonElement Serialize(World world, Entity entity);
		void DeserializeAndApply(World world, Entity entity, JsonElement data);
	}

	private sealed class LocalTransformSceneComponentAdapter : ISceneComponentAdapter
	{
		public JsonElement Serialize(World world, Entity entity)
		{
			var localTransform = world.GetComponent<LocalTransform>(entity);
			return JsonSerializer.SerializeToElement(new LocalTransformComponentData
			{
				LocalPosition = localTransform.LocalPosition,
				LocalRotation = localTransform.LocalRotation,
				LocalScale = localTransform.LocalScale
			}, AssetJson.SerializerOptions);
		}

		public void DeserializeAndApply(World world, Entity entity, JsonElement data)
		{
			var payload = data.Deserialize<LocalTransformComponentData>(AssetJson.SerializerOptions)
			             ?? throw new InvalidOperationException("Failed to deserialize LocalTransform scene data.");
			var transform = Matrix4x4.CreateScale(payload.LocalScale)
			               * Matrix4x4.CreateFromQuaternion(payload.LocalRotation)
			               * Matrix4x4.CreateTranslation(payload.LocalPosition);
			world.AddTransform(entity, transform);
		}
	}

	private sealed class CameraSceneComponentAdapter : ISceneComponentAdapter
	{
		public JsonElement Serialize(World world, Entity entity)
		{
			var camera = world.GetComponent<Camera>(entity);
			return JsonSerializer.SerializeToElement(new CameraComponentData
			{
				ScreenWidth = camera.ScreenResolution.X,
				ScreenHeight = camera.ScreenResolution.Y,
				Fov = camera.Fov,
				NearPlane = camera.NearPlane,
				FarPlane = camera.FarPlane,
				AutoResolution = camera.AutoResolution
			}, AssetJson.SerializerOptions);
		}

		public void DeserializeAndApply(World world, Entity entity, JsonElement data)
		{
			var payload = data.Deserialize<CameraComponentData>(AssetJson.SerializerOptions)
			             ?? throw new InvalidOperationException("Failed to deserialize Camera scene data.");
			var camera = new Camera
			{
				ScreenResolution = new Int2(
					Math.Max(payload.ScreenWidth, 1),
					Math.Max(payload.ScreenHeight, 1)),
				NearPlane = payload.NearPlane,
				FarPlane = payload.FarPlane,
				AutoResolution = payload.AutoResolution
			};
			camera.SetPerspective(payload.Fov > 0.0f ? payload.Fov : 70.0f);
			world.AddComponent(entity, camera);
		}
	}

	private sealed class MeshRendererSceneComponentAdapter : ISceneComponentAdapter
	{
		public JsonElement Serialize(World world, Entity entity)
		{
			var meshRenderer = world.GetComponent<MeshRenderer>(entity);
			return JsonSerializer.SerializeToElement(new MeshRendererComponentData
			{
				MeshAssetId = meshRenderer.MeshAsset.NodeId,
				MaterialAssetId = meshRenderer.MaterialAsset.NodeId
			}, AssetJson.SerializerOptions);
		}

		public void DeserializeAndApply(World world, Entity entity, JsonElement data)
		{
			var payload = data.Deserialize<MeshRendererComponentData>(AssetJson.SerializerOptions)
			             ?? throw new InvalidOperationException("Failed to deserialize MeshRenderer scene data.");
			var meshAsset = new AssetRef<Mesh> { NodeId = payload.MeshAssetId };
			var materialAsset = new AssetRef<Material> { NodeId = payload.MaterialAssetId };
			world.AddComponent(entity, new MeshRenderer
			{
				MeshAsset = meshAsset,
				MaterialAsset = materialAsset,
				Mesh = meshAsset.IsValid ? meshAsset.Asset : null,
				Material = materialAsset.IsValid ? materialAsset.Asset : null
			});
		}
	}

	private sealed class WorldSettingsSceneComponentAdapter : ISceneComponentAdapter
	{
		public JsonElement Serialize(World world, Entity entity)
		{
			var worldSettings = world.GetComponent<WorldSettings>(entity);
			return JsonSerializer.SerializeToElement(new WorldSettingsComponentData
			{
				RenderConfigAssetId = worldSettings.RenderConfigAsset.NodeId
			}, AssetJson.SerializerOptions);
		}

		public void DeserializeAndApply(World world, Entity entity, JsonElement data)
		{
			var payload = data.Deserialize<WorldSettingsComponentData>(AssetJson.SerializerOptions)
			             ?? throw new InvalidOperationException("Failed to deserialize WorldSettings scene data.");
			var worldSettings = new WorldSettings
			{
				RenderConfigAsset = new AssetRef<RenderConfig> { NodeId = payload.RenderConfigAssetId }
			};
			_ = worldSettings.RenderConfigAsset.Asset;
			world.AddComponent(entity, worldSettings);
		}
	}
}
