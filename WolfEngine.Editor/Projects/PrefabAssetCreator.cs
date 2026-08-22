using System.Text.Json;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.UI;

namespace WolfEngine.Editor.Projects;

public interface IPrefabAssetCreator
{
	EditorAssetCreationResult SaveEntityAsPrefab(EditorScene scene, Entity rootEntity, string targetRelativeFolderPath);
}

public sealed class PrefabAssetCreator : IPrefabAssetCreator
{
	private readonly IEditorProjectService _projectService;
	private readonly IAssetMetadataStore _metadataStore;
	private readonly IProjectAssetPipelineService _assetPipelineService;
	private readonly IEditorSceneSnapshotService _sceneSnapshotService;
	private readonly IProjectTypeResolver _typeResolver;

	public PrefabAssetCreator(
		IEditorProjectService projectService,
		IAssetMetadataStore metadataStore,
		IProjectAssetPipelineService assetPipelineService,
		IEditorSceneSnapshotService sceneSnapshotService,
		IProjectTypeResolver typeResolver)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_metadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
		_assetPipelineService = assetPipelineService ?? throw new ArgumentNullException(nameof(assetPipelineService));
		_sceneSnapshotService = sceneSnapshotService ?? throw new ArgumentNullException(nameof(sceneSnapshotService));
		_typeResolver = typeResolver ?? throw new ArgumentNullException(nameof(typeResolver));
	}

	public EditorAssetCreationResult SaveEntityAsPrefab(EditorScene scene, Entity rootEntity, string targetRelativeFolderPath)
	{
		ArgumentNullException.ThrowIfNull(scene);
		if (_projectService.HasOpenProject == false)
		{
			return EditorAssetCreationResult.Failed("Open or create a project before creating prefabs.");
		}

		if (scene.World.IsAlive(rootEntity) == false)
		{
			return EditorAssetCreationResult.Failed("Cannot create a prefab from a deleted entity.");
		}

		var rootEntityId = _sceneSnapshotService.EnsurePersistentEntityId(scene, rootEntity);
		var assetName = scene.World.HasComponent<NameComponent>(rootEntity)
			? scene.World.GetComponent<NameComponent>(rootEntity).Name ?? "New Prefab"
			: "New Prefab";
		if (string.IsNullOrWhiteSpace(assetName))
		{
			assetName = "New Prefab";
		}

		var targetFolderPath = ProjectPathUtility.NormalizeAssetsFolderPath(targetRelativeFolderPath);
		var resolvedAssetName = GetNextPrefabName(SanitizePathSegment(assetName.Trim()), targetFolderPath);
		var relativeAssetPath = $"{targetFolderPath}/{resolvedAssetName}{PrefabAssetFile.FileExtension}";
		var absoluteAssetPath = _projectService.GetAbsolutePath(relativeAssetPath);
		var absoluteMetaPath = absoluteAssetPath + ".meta";

		try
		{
			var entities = new List<Entity>();
			CollectEntitySubtree(scene.World, rootEntity, entities);
			var prefabFile = new PrefabAssetFile
			{
				RootEntityId = rootEntityId,
				Entities = []
			};
			for (var i = 0; i < entities.Count; i++)
			{
				var entity = entities[i];
				var serializedEntity = SerializeEntity(scene, entity, _sceneSnapshotService.EnsurePersistentEntityId(scene, entity));
				if (entity == rootEntity)
				{
					serializedEntity.ParentEntityId = null;
				}

				prefabFile.Entities.Add(serializedEntity);
			}

			Directory.CreateDirectory(_projectService.GetAbsolutePath(targetFolderPath));
			WriteJsonAtomicallyIfChanged(absoluteAssetPath, prefabFile);
			_metadataStore.Save(absoluteMetaPath, new AssetSourceMetaFile
			{
				SourceId = Guid.NewGuid(),
				ImporterId = AssetImporterIds.EditorPrefab,
				ImporterVersion = 1,
				SubAssets =
				[
					new AssetSubAssetManifestEntry
					{
						Key = "main",
						NodeId = Guid.NewGuid(),
						Type = AssetType.Prefab,
						Name = resolvedAssetName
					}
				]
			});
			_projectService.RefreshAssetSource(relativeAssetPath);
			if (_assetPipelineService.TryGetPrimaryNodeIdForRelativeSourcePath(_projectService.ProjectRootPath!, relativeAssetPath, out var prefabNodeId) == false)
			{
				return EditorAssetCreationResult.Failed("Prefab was created, but the pipeline did not produce a prefab node.");
			}

			for (var i = 0; i < entities.Count; i++)
			{
				var entity = entities[i];
				var sourcePath = new List<SavedPrefabLink>
				{
					new()
					{
						PrefabAssetId = prefabNodeId,
						PrefabEntityId = scene.EntityIds[entity]
					}
				};
				if (scene.EntityPrefabSourcePaths.TryGetValue(entity, out var nestedSourcePath))
				{
					sourcePath.AddRange(EditorPrefabUtility.ClonePrefabSourcePath(nestedSourcePath));
				}

				scene.EntityPrefabSourcePaths[entity] = sourcePath;
			}

			return EditorAssetCreationResult.Succeeded(prefabNodeId);
		}
		catch (Exception ex)
		{
			return EditorAssetCreationResult.Failed($"Failed to create prefab: {ex.Message}");
		}
	}

	private SavedEntity SerializeEntity(EditorScene scene, Entity entity, Guid entityId)
	{
		var world = scene.World;
		var hasName = world.HasComponent<NameComponent>(entity);
		var savedEntity = new SavedEntity
		{
			EntityId = entityId,
			ParentEntityId = world.HasComponent<Parent>(entity)
				? _sceneSnapshotService.EnsurePersistentEntityId(scene, world.GetComponent<Parent>(entity).Value)
				: null,
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
				Data = EditorEntityReferenceUtility.SerializeComponentData(
					scene,
					componentType,
					RuntimeComponentAccessor.ReadBoxed(world, entity, componentType))
			});
		}

		if (EditorPrefabUtility.TryResolvePrefabSourceEntity(_projectService, savedEntity, out var sourceEntity))
		{
			savedEntity.PrefabOverrides = EditorPrefabUtility.ComputePrefabOverrides(savedEntity, sourceEntity);
		}

		return savedEntity;
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

	private static void CollectEntitySubtree(World world, Entity entity, List<Entity> entities)
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
			CollectEntitySubtree(world, child, entities);
			child = next;
		}
	}

	private string GetNextPrefabName(string baseName, string targetFolderPath)
	{
		var index = 0;
		while (true)
		{
			var candidateName = index == 0 ? baseName : $"{baseName} {index}";
			var relativeAssetPath = $"{targetFolderPath}/{candidateName}{PrefabAssetFile.FileExtension}";
			var absoluteAssetPath = _projectService.GetAbsolutePath(relativeAssetPath);
			if (File.Exists(absoluteAssetPath) == false)
			{
				return candidateName;
			}

			index++;
		}
	}

	private static string SanitizePathSegment(string value)
	{
		var input = string.IsNullOrWhiteSpace(value) ? "New Prefab" : value.Trim();
		var invalidChars = Path.GetInvalidFileNameChars();
		var chars = input.Select(character => invalidChars.Contains(character) ? '_' : character).ToArray();
		var sanitized = new string(chars).Trim();
		return string.IsNullOrWhiteSpace(sanitized) ? "New Prefab" : sanitized;
	}

	private static void WriteJsonAtomicallyIfChanged<T>(string absolutePath, T value)
	{
		var directory = Path.GetDirectoryName(absolutePath);
		if (string.IsNullOrWhiteSpace(directory) == false)
		{
			Directory.CreateDirectory(directory);
		}

		var json = JsonSerializer.Serialize(value, AssetJson.SerializerOptions);
		if (File.Exists(absolutePath) && string.Equals(File.ReadAllText(absolutePath), json, StringComparison.Ordinal))
		{
			return;
		}

		var tempPath = absolutePath + ".tmp";
		File.WriteAllText(tempPath, json);
		File.Move(tempPath, absolutePath, true);
	}
}
