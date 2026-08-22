using System.Text.Json;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor;

internal static class EditorPrefabUtility
{
	public static bool IsPrefabEntity(EditorScene scene, Entity entity)
	{
		return scene.EntityPrefabSourcePaths.TryGetValue(entity, out var sourcePath) && sourcePath.Count > 0;
	}

	public static bool IsNestedPrefabEntity(EditorScene scene, Entity entity)
	{
		if (IsPrefabEntity(scene, entity) == false || scene.World.HasComponent<Parent>(entity) == false)
		{
			return false;
		}

		var parent = scene.World.GetComponent<Parent>(entity).Value;
		return parent.IsValid && scene.World.IsAlive(parent) && IsPrefabEntity(scene, parent);
	}

	public static bool IsPrefabInstanceRoot(EditorScene scene, Entity entity)
	{
		return IsPrefabEntity(scene, entity) && IsNestedPrefabEntity(scene, entity) == false;
	}

	public static Guid GetPrefabRootAssetId(EditorScene scene, Entity entity)
	{
		return scene.EntityPrefabSourcePaths.TryGetValue(entity, out var sourcePath) && sourcePath.Count > 0
			? sourcePath[0].PrefabAssetId
			: Guid.Empty;
	}

	public static List<SavedPrefabLink> ClonePrefabSourcePath(List<SavedPrefabLink>? sourcePath)
	{
		if (sourcePath is null || sourcePath.Count == 0)
		{
			return [];
		}

		return sourcePath
			.Select(link => new SavedPrefabLink
			{
				PrefabAssetId = link.PrefabAssetId,
				PrefabEntityId = link.PrefabEntityId
			})
			.ToList();
	}

	public static SavedPrefabOverrides ClonePrefabOverrides(SavedPrefabOverrides? source)
	{
		if (source is null)
		{
			return new SavedPrefabOverrides();
		}

		return new SavedPrefabOverrides
		{
			Name = source.Name,
			Enabled = source.Enabled,
			LocalTransform = source.LocalTransform,
			ComponentTypeIds = source.ComponentTypeIds.ToList()
		};
	}

	public static bool TryResolvePrefabSourceEntity(
		IEditorProjectService projectService,
		SavedEntity savedEntity,
		out SavedEntity sourceEntity)
	{
		if (savedEntity.PrefabSourcePath.Count == 0)
		{
			sourceEntity = null!;
			return false;
		}

		return TryResolvePrefabSourceEntity(projectService, savedEntity.PrefabSourcePath[0], new HashSet<Guid>(), out sourceEntity);
	}

	public static SavedEntity MergePrefabSourceEntity(SavedEntity savedEntity, SavedEntity sourceEntity)
	{
		var merged = CloneEntity(savedEntity);
		if (merged.PrefabOverrides.Name == false)
		{
			merged.HasName = sourceEntity.HasName;
			merged.Name = sourceEntity.Name;
		}

		if (merged.PrefabOverrides.Enabled == false)
		{
			merged.Enabled = sourceEntity.Enabled;
		}

		if (merged.PrefabOverrides.LocalTransform == false)
		{
			merged.LocalTransform = sourceEntity.LocalTransform;
		}

		var mergedComponents = new List<SavedComponent>();
		for (var i = 0; i < sourceEntity.Components.Count; i++)
		{
			var sourceComponent = sourceEntity.Components[i];
			var overrideComponent = merged.Components.FirstOrDefault(candidate =>
				string.Equals(candidate.TypeId, sourceComponent.TypeId, StringComparison.Ordinal) ||
				string.Equals(candidate.Type, sourceComponent.Type, StringComparison.Ordinal));
			if (overrideComponent is not null &&
			    (merged.PrefabOverrides.HasComponentOverride(sourceComponent.TypeId) ||
			     merged.PrefabOverrides.HasComponentOverride(sourceComponent.Type)))
			{
				mergedComponents.Add(CloneComponent(overrideComponent));
				continue;
			}

			mergedComponents.Add(CloneComponent(sourceComponent));
		}

		for (var i = 0; i < merged.Components.Count; i++)
		{
			var component = merged.Components[i];
			var hasSourceComponent = sourceEntity.Components.Any(sourceComponent =>
				string.Equals(sourceComponent.TypeId, component.TypeId, StringComparison.Ordinal) ||
				string.Equals(sourceComponent.Type, component.Type, StringComparison.Ordinal));
			if (hasSourceComponent)
			{
				continue;
			}

			mergedComponents.Add(CloneComponent(component));
		}

		merged.Components = mergedComponents;
		return merged;
	}

	public static SavedPrefabOverrides ComputePrefabOverrides(SavedEntity savedEntity, SavedEntity sourceEntity)
	{
		var overrides = new SavedPrefabOverrides
		{
			Name = savedEntity.HasName != sourceEntity.HasName ||
			       string.Equals(savedEntity.Name, sourceEntity.Name, StringComparison.Ordinal) == false,
			Enabled = savedEntity.Enabled != sourceEntity.Enabled,
			LocalTransform = string.Equals(
				SerializeValue(savedEntity.LocalTransform),
				SerializeValue(sourceEntity.LocalTransform),
				StringComparison.Ordinal) == false,
			ComponentTypeIds = []
		};

		for (var i = 0; i < savedEntity.Components.Count; i++)
		{
			var component = savedEntity.Components[i];
			var sourceComponent = sourceEntity.Components.FirstOrDefault(candidate =>
				string.Equals(candidate.TypeId, component.TypeId, StringComparison.Ordinal) ||
				string.Equals(candidate.Type, component.Type, StringComparison.Ordinal));
			if (sourceComponent is null ||
			    string.Equals(SerializeValue(component.Data), SerializeValue(sourceComponent.Data), StringComparison.Ordinal) == false)
			{
				overrides.ComponentTypeIds.Add(string.IsNullOrWhiteSpace(component.TypeId) ? component.Type : component.TypeId);
			}
		}

		return overrides;
	}

	public static SavedEntity CloneEntity(SavedEntity source)
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
			PrefabSourcePath = ClonePrefabSourcePath(source.PrefabSourcePath),
			PrefabOverrides = ClonePrefabOverrides(source.PrefabOverrides),
			Components = source.Components.Select(CloneComponent).ToList()
		};
	}

	public static SavedComponent CloneComponent(SavedComponent source)
	{
		return new SavedComponent
		{
			Type = source.Type,
			TypeId = source.TypeId,
			Data = source.Data.Clone()
		};
	}

	private static bool TryResolvePrefabSourceEntity(
		IEditorProjectService projectService,
		SavedPrefabLink sourceLink,
		HashSet<Guid> prefabAssetStack,
		out SavedEntity sourceEntity)
	{
		sourceEntity = null!;
		if (sourceLink.PrefabAssetId == Guid.Empty ||
		    sourceLink.PrefabEntityId == Guid.Empty ||
		    projectService.TryGetAsset(sourceLink.PrefabAssetId, out var prefabAsset) == false ||
		    prefabAsset.Type != AssetType.Prefab)
		{
			return false;
		}

		if (prefabAssetStack.Add(sourceLink.PrefabAssetId) == false)
		{
			throw new InvalidOperationException($"Cyclic prefab nesting detected while resolving prefab '{sourceLink.PrefabAssetId}'.");
		}

		var prefabFile = PrefabAssetFile.Load(projectService.GetAbsolutePath(prefabAsset.RelativeAssetPath));
		var source = prefabFile.Entities.FirstOrDefault(entity => entity.EntityId == sourceLink.PrefabEntityId);
		if (source is null)
		{
			prefabAssetStack.Remove(sourceLink.PrefabAssetId);
			return false;
		}

		sourceEntity = CloneEntity(source);
		if (sourceEntity.PrefabSourcePath.Count > 0 &&
		    TryResolvePrefabSourceEntity(projectService, sourceEntity.PrefabSourcePath[0], prefabAssetStack, out var nestedSource))
		{
			sourceEntity = MergePrefabSourceEntity(sourceEntity, nestedSource);
		}

		prefabAssetStack.Remove(sourceLink.PrefabAssetId);
		return true;
	}

	private static string SerializeValue<T>(T value)
	{
		return JsonSerializer.Serialize(value, AssetJson.SerializerOptions);
	}
}
