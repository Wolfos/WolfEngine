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

	/// <summary>
	/// Translation between one prefab instance's entities and the prefab file they came from. Component data
	/// inside a prefab addresses the prefab's own entities, while a scene addresses them by the persistent ids
	/// its instance was given, so every reference crossing that boundary has to be translated. The map is per
	/// instance: a scene can hold several instances of the same prefab, each with its own scene ids.
	/// Prefab entities the instance no longer contains, and references to entities outside the prefab, are
	/// absent from the map and are left pointing where they already point.
	/// </summary>
	public readonly record struct PrefabInstanceEntityIdMap
	{
		private static readonly Dictionary<Guid, Guid> NoEntityIds = new();
		private readonly IReadOnlyDictionary<Guid, Guid>? _prefabToScene;

		public PrefabInstanceEntityIdMap(IReadOnlyDictionary<Guid, Guid> prefabToScene)
		{
			_prefabToScene = prefabToScene;
		}

		/// <summary>An instance that translates nothing, leaving every reference where it points.</summary>
		public static PrefabInstanceEntityIdMap Empty => default;

		/// <summary>Reads prefab data into a scene: prefab entity id to the id the instance gave that entity.</summary>
		public IReadOnlyDictionary<Guid, Guid> PrefabToScene => _prefabToScene ?? NoEntityIds;

		/// <summary>Writes scene data back into a prefab. Builds the inverse on demand, so hold on to the result.</summary>
		public IReadOnlyDictionary<Guid, Guid> SceneToPrefab
		{
			get
			{
				var inverted = new Dictionary<Guid, Guid>(PrefabToScene.Count);
				foreach (var entry in PrefabToScene)
				{
					inverted[entry.Value] = entry.Key;
				}

				return inverted;
			}
		}
	}

	/// <summary>
	/// Builds one <see cref="PrefabInstanceEntityIdMap"/> per prefab instance in a loaded set of cells, keyed by
	/// the persistent id of every entity in that instance.
	/// </summary>
	public static Dictionary<Guid, PrefabInstanceEntityIdMap> BuildPrefabInstanceEntityIdMaps(
		IEnumerable<SavedEntity> savedEntities)
	{
		ArgumentNullException.ThrowIfNull(savedEntities);
		var members = new List<PrefabInstanceMember>();
		var parentEntityIds = new Dictionary<Guid, Guid>();
		var prefabAssetIds = new Dictionary<Guid, Guid>();
		foreach (var savedEntity in savedEntities)
		{
			if (savedEntity.EntityId == Guid.Empty)
			{
				continue;
			}

			parentEntityIds[savedEntity.EntityId] = savedEntity.ParentEntityId ?? Guid.Empty;
			if (savedEntity.PrefabSourcePath.Count == 0)
			{
				continue;
			}

			var link = savedEntity.PrefabSourcePath[0];
			prefabAssetIds[savedEntity.EntityId] = link.PrefabAssetId;
			members.Add(new PrefabInstanceMember(savedEntity.EntityId, link.PrefabAssetId, link.PrefabEntityId));
		}

		return BuildPrefabInstanceEntityIdMaps(members, parentEntityIds, prefabAssetIds);
	}

	/// <summary>
	/// Builds one <see cref="PrefabInstanceEntityIdMap"/> per prefab instance in a live scene, keyed by the
	/// persistent id of every entity in that instance. Entities that have not been assigned a persistent id
	/// yet cannot be referenced from saved data, so they are left out.
	/// </summary>
	public static Dictionary<Guid, PrefabInstanceEntityIdMap> BuildPrefabInstanceEntityIdMaps(EditorScene scene)
	{
		ArgumentNullException.ThrowIfNull(scene);
		var members = new List<PrefabInstanceMember>();
		var parentEntityIds = new Dictionary<Guid, Guid>();
		var prefabAssetIds = new Dictionary<Guid, Guid>();
		foreach (var entry in scene.EntityIds)
		{
			var entity = entry.Key;
			var entityId = entry.Value;
			if (entityId == Guid.Empty || scene.World.IsAlive(entity) == false)
			{
				continue;
			}

			parentEntityIds[entityId] = TryGetParentEntityId(scene, entity);
			if (scene.EntityPrefabSourcePaths.TryGetValue(entity, out var sourcePath) == false || sourcePath.Count == 0)
			{
				continue;
			}

			prefabAssetIds[entityId] = sourcePath[0].PrefabAssetId;
			members.Add(new PrefabInstanceMember(entityId, sourcePath[0].PrefabAssetId, sourcePath[0].PrefabEntityId));
		}

		return BuildPrefabInstanceEntityIdMaps(members, parentEntityIds, prefabAssetIds);
	}

	/// <summary>
	/// Rewrites the entity references in a prefab source entity's components so they address the instance the
	/// data is being merged into.
	/// </summary>
	public static SavedEntity RemapPrefabSourceEntityReferences(
		SavedEntity sourceEntity,
		PrefabInstanceEntityIdMap entityIdMap)
	{
		return RemapEntityReferences(sourceEntity, entityIdMap.PrefabToScene);
	}

	public static SavedEntity RemapEntityReferences(SavedEntity entity, IReadOnlyDictionary<Guid, Guid> entityIdMap)
	{
		ArgumentNullException.ThrowIfNull(entity);
		ArgumentNullException.ThrowIfNull(entityIdMap);
		if (entityIdMap.Count == 0)
		{
			return entity;
		}

		var remapped = CloneEntity(entity);
		for (var i = 0; i < remapped.Components.Count; i++)
		{
			remapped.Components[i].Data =
				EditorEntityReferenceUtility.RemapEntityReferences(remapped.Components[i].Data, entityIdMap);
		}

		return remapped;
	}

	private static Guid TryGetParentEntityId(EditorScene scene, Entity entity)
	{
		if (scene.World.HasComponent<Parent>(entity) == false)
		{
			return Guid.Empty;
		}

		var parent = scene.World.GetComponent<Parent>(entity).Value;
		return parent.IsValid && scene.World.IsAlive(parent) && scene.EntityIds.TryGetValue(parent, out var parentEntityId)
			? parentEntityId
			: Guid.Empty;
	}

	private static Dictionary<Guid, PrefabInstanceEntityIdMap> BuildPrefabInstanceEntityIdMaps(
		List<PrefabInstanceMember> members,
		Dictionary<Guid, Guid> parentEntityIds,
		Dictionary<Guid, Guid> prefabAssetIds)
	{
		var membersByInstanceRoot = new Dictionary<Guid, List<PrefabInstanceMember>>();
		var instanceRootByEntityId = new Dictionary<Guid, Guid>(members.Count);
		for (var i = 0; i < members.Count; i++)
		{
			var member = members[i];
			var instanceRootId = FindInstanceRootEntityId(member, parentEntityIds, prefabAssetIds);
			instanceRootByEntityId[member.EntityId] = instanceRootId;
			if (membersByInstanceRoot.TryGetValue(instanceRootId, out var instanceMembers) == false)
			{
				instanceMembers = [];
				membersByInstanceRoot[instanceRootId] = instanceMembers;
			}

			instanceMembers.Add(member);
		}

		var mapsByInstanceRoot = new Dictionary<Guid, PrefabInstanceEntityIdMap>(membersByInstanceRoot.Count);
		foreach (var entry in membersByInstanceRoot)
		{
			var prefabToScene = new Dictionary<Guid, Guid>(entry.Value.Count);
			for (var i = 0; i < entry.Value.Count; i++)
			{
				var member = entry.Value[i];
				if (member.PrefabEntityId != Guid.Empty)
				{
					prefabToScene[member.PrefabEntityId] = member.EntityId;
				}
			}

			mapsByInstanceRoot[entry.Key] = new PrefabInstanceEntityIdMap(prefabToScene);
		}

		var mapsByEntityId = new Dictionary<Guid, PrefabInstanceEntityIdMap>(members.Count);
		foreach (var entry in instanceRootByEntityId)
		{
			mapsByEntityId[entry.Key] = mapsByInstanceRoot[entry.Value];
		}

		return mapsByEntityId;
	}

	/// <summary>
	/// Walks up to the outermost entity that still belongs to the same prefab instance. Ancestors from another
	/// prefab, or none at all, end the walk, which keeps sibling instances of one prefab in separate maps.
	/// </summary>
	private static Guid FindInstanceRootEntityId(
		PrefabInstanceMember member,
		Dictionary<Guid, Guid> parentEntityIds,
		Dictionary<Guid, Guid> prefabAssetIds)
	{
		var instanceRootId = member.EntityId;
		var visited = new HashSet<Guid> { instanceRootId };
		while (parentEntityIds.TryGetValue(instanceRootId, out var parentEntityId) &&
		       parentEntityId != Guid.Empty &&
		       prefabAssetIds.TryGetValue(parentEntityId, out var parentPrefabAssetId) &&
		       parentPrefabAssetId == member.PrefabAssetId &&
		       visited.Add(parentEntityId))
		{
			instanceRootId = parentEntityId;
		}

		return instanceRootId;
	}

	private readonly record struct PrefabInstanceMember(Guid EntityId, Guid PrefabAssetId, Guid PrefabEntityId);

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

		var prefabFile = PrefabAssetFile.Load(projectService.GetAbsoluteAssetPath(
			sourceLink.PrefabAssetId, prefabAsset.RelativeAssetPath));
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
