using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.UI;
using WolfEngine.Importing;

namespace WolfEngine.Editor.Projects;

public sealed class DataAssetRuntimeResolver : IDataAssetRuntimeResolver
{
	private readonly IDataAssetStore _dataAssetStore;

	public DataAssetRuntimeResolver(IDataAssetStore dataAssetStore)
	{
		_dataAssetStore = dataAssetStore ?? throw new ArgumentNullException(nameof(dataAssetStore));
	}

	public object? Resolve(RuntimeAssetResolveContext context)
	{
		var loadedAsset = _dataAssetStore.LoadAsset(context.GetAbsolutePath(context.Asset.RelativeAssetPath)).Asset;
		if (context.RuntimeType.IsInstanceOfType(loadedAsset) == false)
		{
			throw new InvalidOperationException(
				$"Data asset '{context.AssetId}' resolved to '{loadedAsset.GetType().FullName}', which cannot be assigned to '{context.RuntimeType.FullName}'.");
		}

		return loadedAsset;
	}
}

public sealed class TerrainAssetRuntimeResolver : ITerrainAssetRuntimeResolver
{
	public object Resolve(RuntimeAssetResolveContext context)
	{
		var terrainAsset = TerrainAssetSerializer.Read(
			context.GetAbsolutePath(context.Asset.RelativeAssetPath),
			context.Asset.Name);
		return terrainAsset;
	}
}

public sealed class MaterialRuntimeAssetResolver : IMaterialRuntimeAssetResolver
{
	private readonly IMaterialAssetStore _materialAssetStore;
	private readonly IMaterialTypeRegistry _materialTypeRegistry;
	private readonly IMaterialFactory _materialFactory;

	public MaterialRuntimeAssetResolver(
		IMaterialAssetStore materialAssetStore,
		IMaterialTypeRegistry materialTypeRegistry,
		IMaterialFactory materialFactory)
	{
		_materialAssetStore = materialAssetStore ?? throw new ArgumentNullException(nameof(materialAssetStore));
		_materialTypeRegistry = materialTypeRegistry ?? throw new ArgumentNullException(nameof(materialTypeRegistry));
		_materialFactory = materialFactory ?? throw new ArgumentNullException(nameof(materialFactory));
	}

	public object Resolve(RuntimeAssetResolveContext context)
	{
		var materialAsset = _materialAssetStore.LoadAsset(context.GetAbsolutePath(context.Asset.RelativeAssetPath));
		var descriptor = _materialTypeRegistry.GetDescriptor(materialAsset.MaterialType);
		var properties = materialAsset.GetActiveProperties();

		return _materialFactory.GetMaterial(
			shader: descriptor.ShaderPath,
			color: properties.BaseColor,
			metallicFactor: properties.MetallicFactor,
			roughnessFactor: properties.RoughnessFactor,
			normalScale: properties.NormalScale,
			emissiveFactor: properties.EmissiveFactor,
			emissiveIntensity: properties.EmissiveIntensity,
			albedoTexture: ResolveTexture(properties.Textures.Albedo),
			ormTexture: ResolveTexture(properties.Textures.Orm),
			normalTexture: ResolveTexture(properties.Textures.Normal),
			emissiveTexture: ResolveTexture(properties.Textures.Emissive),
			alphaMode: descriptor.RuntimeAlphaMode,
			alphaCutoff: materialAsset.AlphaCutoff,
			uvOffsetScale: properties.UvOffsetScale,
			doubleSided: materialAsset.DoubleSided);
	}

	private static Texture? ResolveTexture(AssetRef<Texture> reference)
	{
		return reference.NodeId == Guid.Empty ? null : reference.Asset;
	}
}

public sealed class TextureRuntimeAssetResolver : ITextureRuntimeAssetResolver
{
	private readonly ITextureFactory _textureFactory;
	private readonly IRuntimeArtifactTargetProvider _targetProvider;

	public TextureRuntimeAssetResolver(
		ITextureFactory textureFactory,
		IRuntimeArtifactTargetProvider targetProvider)
	{
		_textureFactory = textureFactory ?? throw new ArgumentNullException(nameof(textureFactory));
		_targetProvider = targetProvider ?? throw new ArgumentNullException(nameof(targetProvider));
	}

	public object Resolve(RuntimeAssetResolveContext context)
	{
		var summary = context.Asset.GetRequiredSummary<TextureAssetSummary>();
		var runtimeTextureName = GetRuntimeTextureName(context.AssetId, context.Asset.Name);

		var targetArtifact = context.Asset.Artifacts
			.Where(artifact => string.Equals(artifact.Kind, "RuntimeTexture", StringComparison.Ordinal))
			.FirstOrDefault(artifact => string.Equals(artifact.Target, _targetProvider.CurrentTarget, StringComparison.OrdinalIgnoreCase));
		if (targetArtifact is not null)
		{
			var runtimeTexture = TextureArtifactSerializer.Read(
				context.GetAbsolutePath(targetArtifact.RelativePath),
				runtimeTextureName);
			return _textureFactory.GetTexture(runtimeTexture);
		}

		if (string.IsNullOrWhiteSpace(summary.RelativeRuntimeArtifactPath) == false)
		{
			var runtimeTexture = TextureArtifactSerializer.Read(
				context.GetAbsolutePath(summary.RelativeRuntimeArtifactPath),
				runtimeTextureName);
			return _textureFactory.GetTexture(runtimeTexture);
		}

		if (string.IsNullOrWhiteSpace(summary.RelativeImportedPath) == false)
		{
			var importedTexture = ImportedTextureSerializer.Read(
				context.GetAbsolutePath(summary.RelativeImportedPath),
				runtimeTextureName);
			return _textureFactory.GetTexture(importedTexture);
		}

		if (string.IsNullOrWhiteSpace(summary.RelativeSourceAssetPath) == false)
		{
			return _textureFactory.LoadFromFile(
				context.GetAbsolutePath(summary.RelativeSourceAssetPath),
				StbImageLoader.IsSrgb(summary.Semantic));
		}

		throw new InvalidOperationException(
			$"Texture node '{context.AssetId}' does not expose a runtime artifact, imported texture, or source file.");
	}

	private static string GetRuntimeTextureName(Guid assetId, string assetName)
	{
		return string.IsNullOrWhiteSpace(assetName)
			? assetId.ToString("D")
			: $"{assetId:D}:{assetName}";
	}
}

public sealed class ColorLookupTableRuntimeAssetResolver : IColorLookupTableRuntimeResolver
{
	private readonly ITextureFactory _textureFactory;

	public ColorLookupTableRuntimeAssetResolver(ITextureFactory textureFactory)
	{
		_textureFactory = textureFactory ?? throw new ArgumentNullException(nameof(textureFactory));
	}

	public object Resolve(RuntimeAssetResolveContext context)
	{
		var artifact = context.Asset.Artifacts.FirstOrDefault(artifact =>
			               string.Equals(artifact.Kind, ColorLookupTableArtifactSerializer.ArtifactKind, StringComparison.Ordinal))
		               ?? throw new InvalidOperationException(
			               $"Colour lookup table '{context.AssetId}' does not expose a runtime artifact.");
		var data = ColorLookupTableArtifactSerializer.Read(context.GetAbsolutePath(artifact.RelativePath));
		return ColorLookupTable.Create(GetRuntimeName(context.AssetId), data, _textureFactory);
	}

	/// <summary>
	/// The texture factory caches by name, so the name is keyed by asset id alone and cannot collide with
	/// 2D texture names; a reimport of the same asset then updates the cached volume in place.
	/// </summary>
	internal static string GetRuntimeName(Guid assetId) => $"lut:{assetId:D}";
}

public sealed class MeshRuntimeAssetResolver : IMeshRuntimeAssetResolver
{
	public object Resolve(RuntimeAssetResolveContext context)
	{
		var summary = context.Asset.GetRequiredSummary<MeshAssetSummary>();
		if (summary.CanonicalMeshNodeId != Guid.Empty)
		{
			return context.ResolveAsset(summary.CanonicalMeshNodeId, typeof(Mesh))
			       ?? throw new InvalidOperationException($"Canonical mesh '{summary.CanonicalMeshNodeId}' is missing.");
		}
		var absoluteMeshPath = context.GetAbsolutePath(summary.RelativeImportedMeshPath);
		var meshFile = ImportedMeshSerializer.Read(absoluteMeshPath);
		var hasSkin = meshFile.BoneIndices.Length > 0 && meshFile.BoneWeights.Length > 0;
		return new Mesh(
			meshFile.Vertices,
			meshFile.Indices,
			meshFile.Normals,
			meshFile.UVs,
			meshFile.Tangents,
			hasSkin ? meshFile.BoneIndices : null,
			hasSkin ? meshFile.BoneWeights : null,
			meshFile.ShadowOpaqueIndices, meshFile.ShadowAlphaTestIndices);
	}
}

public sealed class SkeletonRuntimeAssetResolver : ISkeletonRuntimeAssetResolver
{
	public object Resolve(RuntimeAssetResolveContext context)
	{
		var summary = context.Asset.GetRequiredSummary<SkeletonAssetSummary>();
		var absolutePath = context.GetAbsolutePath(summary.RelativeImportedSkeletonPath);
		return SkeletonSerializer.Read(absolutePath).ToSkeleton();
	}
}

public sealed class AnimationClipRuntimeAssetResolver : IAnimationClipRuntimeAssetResolver
{
	public object Resolve(RuntimeAssetResolveContext context)
	{
		var summary = context.Asset.GetRequiredSummary<AnimationClipAssetSummary>();
		var absolutePath = context.GetAbsolutePath(summary.RelativeImportedClipPath);
		return AnimationClipSerializer.Read(absolutePath).ToClip();
	}
}

public sealed class AnimationAssetRuntimeResolver : IAnimationAssetRuntimeResolver
{
    public object Resolve(RuntimeAssetResolveContext context) => global::WolfEngine.Animation.AnimationAssetJson.Read(
        context.GetAbsolutePath(context.Asset.RelativeAssetPath), context.RuntimeType);
}

/// <summary>
/// Builds the spawn template gameplay code instantiates in Play mode. Nested prefab instances are merged with
/// their current source, as placing the prefab in a scene does, so Play mode spawns what the editor shows.
/// </summary>
public sealed class PrefabRuntimeAssetResolver : IPrefabRuntimeAssetResolver
{
	private readonly IEditorProjectService _projectService;
	private readonly IProjectTypeResolver? _typeResolver;
	private readonly IEditorNotificationService? _notificationService;

	public PrefabRuntimeAssetResolver(
		IEditorProjectService projectService,
		IProjectTypeResolver? typeResolver = null,
		IEditorNotificationService? notificationService = null)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_typeResolver = typeResolver;
		_notificationService = notificationService;
	}

	public object Resolve(RuntimeAssetResolveContext context)
	{
		var prefabFile = PrefabAssetFile.Load(context.GetAbsolutePath(context.Asset.RelativeAssetPath));
		var document = new PrefabDocument
		{
			Version = PrefabDocument.CurrentVersion,
			RootEntityId = prefabFile.RootEntityId,
			Entities = new List<PrefabDocumentEntity>(prefabFile.Entities.Count)
		};
		for (var i = 0; i < prefabFile.Entities.Count; i++)
		{
			var savedEntity = prefabFile.Entities[i];
			if (EditorPrefabUtility.TryResolvePrefabSourceEntity(_projectService, savedEntity, out var sourceEntity))
			{
				savedEntity = EditorPrefabUtility.MergePrefabSourceEntity(savedEntity, sourceEntity);
			}

			document.Entities.Add(ToDocumentEntity(savedEntity));
		}

		var skippedComponents = new List<string>();
		var prefab = Prefab.Create(context.AssetId, document, component =>
		{
			if (TryResolveComponentType(component, out var componentType) &&
			    componentType.IsValueType &&
			    typeof(IEntityComponent).IsAssignableFrom(componentType))
			{
				return componentType;
			}

			skippedComponents.Add(string.IsNullOrWhiteSpace(component.TypeId) ? component.Type : component.TypeId);
			return null;
		});
		ReportSkippedComponents(context.Asset.RelativeAssetPath, skippedComponents);
		return prefab;
	}

	private static PrefabDocumentEntity ToDocumentEntity(SavedEntity savedEntity)
	{
		var components = new List<PrefabDocumentComponent>(savedEntity.Components.Count);
		for (var i = 0; i < savedEntity.Components.Count; i++)
		{
			var component = savedEntity.Components[i];
			components.Add(new PrefabDocumentComponent
			{
				Type = component.Type,
				TypeId = component.TypeId,
				Data = component.Data
			});
		}

		return new PrefabDocumentEntity
		{
			EntityId = savedEntity.EntityId,
			ParentEntityId = savedEntity.ParentEntityId,
			HasName = savedEntity.HasName,
			Name = savedEntity.Name,
			Enabled = savedEntity.Enabled,
			LocalTransform = savedEntity.LocalTransform,
			Components = components
		};
	}

	private bool TryResolveComponentType(PrefabDocumentComponent component, out Type componentType)
	{
		if (_typeResolver?.TryResolveStableTypeId(component.TypeId, out componentType) == true)
		{
			return true;
		}

		if (_typeResolver?.TryResolveType(component.Type, out componentType) == true)
		{
			return true;
		}

		return ProjectTypeResolverUtility.TryResolveFromLoadedAssemblies(component.TypeId, out componentType) ||
		       ProjectTypeResolverUtility.TryResolveFromLoadedAssemblies(component.Type, out componentType);
	}

	// Play mode keeps running without the missing components, matching how the editor places the prefab.
	private void ReportSkippedComponents(string prefabRelativePath, List<string> skippedComponents)
	{
		if (skippedComponents.Count == 0)
		{
			return;
		}

		var message =
			$"Prefab '{prefabRelativePath}' spawns without {skippedComponents.Count} component(s) that could not be resolved: " +
			$"{string.Join(", ", skippedComponents.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))}. " +
			"Gameplay components need the gameplay assembly to be built and loaded.";
		Console.WriteLine($"[Prefab] {message}");
		_notificationService?.ReportError(message);
	}
}
