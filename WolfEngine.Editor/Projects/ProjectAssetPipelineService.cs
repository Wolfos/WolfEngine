using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.Importing;
using WolfEngine.ECS;
using WolfEngine.Editor.UI;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Audio;
using ImportImageLoader = WolfEngine.Importing.IImageLoader;

namespace WolfEngine.Editor.Projects;

public interface IProjectAssetPipelineService
{
	void InitializeProject(string projectRootPath);
	AssetDatabase RefreshProject(string projectRootPath);
	AssetDatabase RebuildProject(string projectRootPath);
	AssetDatabase RefreshProjectIncremental(string projectRootPath);
	AssetPipelineRefreshResult RefreshProjectIncrementalWithChanges(string projectRootPath) =>
		new(RefreshProjectIncremental(projectRootPath), Array.Empty<Guid>());
	IReadOnlyCollection<Guid> ExpandInvalidationClosure(string projectRootPath, IEnumerable<Guid> changedNodeIds);
	void RemoveDeletedSource(string projectRootPath, string relativeSourcePath);
	void RemoveDeletedSourcesUnderFolder(string projectRootPath, string relativeFolderPath);
	void ReimportSource(string projectRootPath, string relativeSourcePath);
	AssetDatabase LoadDatabase(string projectRootPath);
	bool TryGetAsset(string projectRootPath, Guid nodeId, out AssetDatabaseEntry asset);
	bool TryGetPrimaryNodeIdForRelativeSourcePath(string projectRootPath, string relativeSourcePath, out Guid nodeId);
	void AssignSceneCellAssetIds(
		string projectRootPath,
		string relativeScenePath,
		EditorSceneAssetFile sceneAsset,
		string globalCellPath,
		IReadOnlyDictionary<Int2, string> spatialCellPaths);
	AssetImportResult ImportExternalSource(string projectRootPath, string absoluteSourcePath);
	void InstantiateImportedModel(string projectRootPath, Guid modelNodeId, World world, Vector3? spawnPosition = null);
	void InstantiatePrefab(string projectRootPath, Guid prefabNodeId, EditorScene scene, Vector3? spawnPosition = null);
}

public readonly record struct AssetPipelineRefreshResult(
	AssetDatabase Database,
	IReadOnlyCollection<Guid> ReimportedNodeIds);

public sealed class ProjectAssetPipelineService : IProjectAssetPipelineService
{
	private readonly IAssetPipelineIndex _index;
	private readonly IAssetMetadataStore _metadataStore;
	private readonly ImportImageLoader _imageLoader;
	private readonly IDataAssetStore _dataAssetStore;
	private readonly IMaterialAssetStore _materialAssetStore;
	private readonly IThreeDFileImporter _threeDFileImporter;
	private readonly ITextureGpuCompressionService _textureGpuCompressionService;
	private readonly IReadOnlyList<AssetImporterDescriptor> _importers;
	private Stopwatch? _libraryBuildStopwatch;

	public ProjectAssetPipelineService(
		IAssetPipelineIndex index,
		IAssetMetadataStore metadataStore,
		ImportImageLoader imageLoader,
		IDataAssetStore dataAssetStore,
		IMaterialAssetStore materialAssetStore,
		IThreeDFileImporter threeDFileImporter)
		: this(
			index,
			metadataStore,
			imageLoader,
			dataAssetStore,
			materialAssetStore,
			threeDFileImporter,
			new UnsupportedTextureGpuCompressionService())
	{
	}

	public ProjectAssetPipelineService(
		IAssetPipelineIndex index,
		IAssetMetadataStore metadataStore,
		ImportImageLoader imageLoader,
		IDataAssetStore dataAssetStore,
		IMaterialAssetStore materialAssetStore,
		IThreeDFileImporter threeDFileImporter,
		ITextureGpuCompressionService textureGpuCompressionService)
	{
		_index = index ?? throw new ArgumentNullException(nameof(index));
		_metadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
		_imageLoader = imageLoader ?? throw new ArgumentNullException(nameof(imageLoader));
		_dataAssetStore = dataAssetStore ?? throw new ArgumentNullException(nameof(dataAssetStore));
		_materialAssetStore = materialAssetStore ?? throw new ArgumentNullException(nameof(materialAssetStore));
		_threeDFileImporter = threeDFileImporter ?? throw new ArgumentNullException(nameof(threeDFileImporter));
		_textureGpuCompressionService = textureGpuCompressionService ??
		                                throw new ArgumentNullException(nameof(textureGpuCompressionService));
		_importers = CreateImporters();
	}

	public void InitializeProject(string projectRootPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
		Directory.CreateDirectory(AssetPipelinePaths.GetAssetsPath(projectRootPath));
		Directory.CreateDirectory(AssetPipelinePaths.GetLibraryPath(projectRootPath));
		Directory.CreateDirectory(AssetPipelinePaths.GetImportedRoot(projectRootPath));
		Directory.CreateDirectory(AssetPipelinePaths.GetArtifactsRoot(projectRootPath));
		_index.Initialize(projectRootPath);
	}

	public AssetDatabase RefreshProject(string projectRootPath)
	{
		return RebuildProject(projectRootPath);
	}

	public AssetDatabase RebuildProject(string projectRootPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
		BeginLibraryBuildDiagnostics("Rebuild started.");
		try
		{
			LogLibraryBuildStage("Recreating Library directory.");
			RecreateLibrary(projectRootPath);
			LogLibraryBuildStage("Library directory recreated; beginning source import.");
			var database = ImportAllSupportedSources(projectRootPath, loadExistingSources: false);
			LogLibraryBuildStage($"Rebuild completed with {database.Assets.Count} asset nodes.");
			return database;
		}
		catch
		{
			LogLibraryBuildStage("Rebuild failed.");
			throw;
		}
		finally
		{
			_libraryBuildStopwatch = null;
		}
	}

	public AssetDatabase RefreshProjectIncremental(string projectRootPath)
	{
		return RefreshProjectIncrementalWithChanges(projectRootPath).Database;
	}

	public AssetPipelineRefreshResult RefreshProjectIncrementalWithChanges(string projectRootPath)
	{
		var reimportedSourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var database = ImportAllSupportedSources(projectRootPath, loadExistingSources: true, reimportedSourcePaths);
		var reimportedNodeIds = database.Assets
			.Where(asset => reimportedSourcePaths.Contains(asset.RelativeSourcePath))
			.Select(asset => asset.Id)
			.ToArray();
		return new AssetPipelineRefreshResult(database, reimportedNodeIds);
	}

	public IReadOnlyCollection<Guid> ExpandInvalidationClosure(string projectRootPath, IEnumerable<Guid> changedNodeIds)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
		ArgumentNullException.ThrowIfNull(changedNodeIds);

		InitializeProject(projectRootPath);

		var invalidatedNodeIds = changedNodeIds
			.Where(nodeId => nodeId != Guid.Empty)
			.ToHashSet();
		if (invalidatedNodeIds.Count == 0)
		{
			return [];
		}

		var reverseDependencies = new Dictionary<Guid, List<Guid>>();
		foreach (var dependency in _index.GetDependencies(projectRootPath))
		{
			if (reverseDependencies.TryGetValue(dependency.ToNodeId, out var dependents) == false)
			{
				dependents = [];
				reverseDependencies[dependency.ToNodeId] = dependents;
			}

			dependents.Add(dependency.FromNodeId);
		}

		var queue = new Queue<Guid>(invalidatedNodeIds);
		while (queue.Count > 0)
		{
			var nodeId = queue.Dequeue();
			if (reverseDependencies.TryGetValue(nodeId, out var dependents) == false)
			{
				continue;
			}

			for (var i = 0; i < dependents.Count; i++)
			{
				if (invalidatedNodeIds.Add(dependents[i]))
				{
					queue.Enqueue(dependents[i]);
				}
			}
		}

		return invalidatedNodeIds.ToArray();
	}

	public void RemoveDeletedSource(string projectRootPath, string relativeSourcePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(relativeSourcePath);
		InitializeProject(projectRootPath);

		var normalizedRelativePath = NormalizeRelativePath(relativeSourcePath);
		if (_index.TryGetSourceByRelativePath(projectRootPath, normalizedRelativePath, out var existingSource) == false)
		{
			return;
		}

		DeleteSourceArtifacts(projectRootPath, existingSource.SourceId);
		_index.DeleteSource(projectRootPath, existingSource.SourceId);
	}

	public void RemoveDeletedSourcesUnderFolder(string projectRootPath, string relativeFolderPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(relativeFolderPath);
		InitializeProject(projectRootPath);

		var normalizedFolderPath = NormalizeRelativePath(relativeFolderPath).TrimEnd('/');
		var sourcesToDelete = _index.GetSources(projectRootPath)
			.Where(source =>
				source.RelativeSourcePath.StartsWith(normalizedFolderPath + "/", StringComparison.OrdinalIgnoreCase))
			.ToList();

		for (var i = 0; i < sourcesToDelete.Count; i++)
		{
			DeleteSourceArtifacts(projectRootPath, sourcesToDelete[i].SourceId);
			_index.DeleteSource(projectRootPath, sourcesToDelete[i].SourceId);
		}
	}

	public void ReimportSource(string projectRootPath, string relativeSourcePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(relativeSourcePath);
		InitializeProject(projectRootPath);

		var normalizedRelativePath = NormalizeRelativePath(relativeSourcePath);
		_index.TryGetSourceByRelativePath(projectRootPath, normalizedRelativePath, out var existingSource);
		var absoluteSourcePath = GetAbsolutePath(projectRootPath, normalizedRelativePath);
		if (File.Exists(absoluteSourcePath) == false)
		{
			if (existingSource is not null)
			{
				DeleteSourceArtifacts(projectRootPath, existingSource.SourceId);
				_index.DeleteSource(projectRootPath, existingSource.SourceId);
			}

			return;
		}

		if (TryGetImporterForPath(normalizedRelativePath, out _) == false)
		{
			throw new InvalidOperationException($"Unsupported asset source '{normalizedRelativePath}'.");
		}

		ImportSource(projectRootPath, absoluteSourcePath, normalizedRelativePath, existingSource);
	}

	public AssetDatabase LoadDatabase(string projectRootPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
		_index.Initialize(projectRootPath);
		var nodes = _index.GetNodes(projectRootPath);
		return new AssetDatabase
		{
			Assets = nodes.Select(node => CreateDatabaseEntry(projectRootPath, node)).ToList()
		};
	}

	public bool TryGetAsset(string projectRootPath, Guid nodeId, out AssetDatabaseEntry asset)
	{
		if (_index.TryGetNode(projectRootPath, nodeId, out var node) == false)
		{
			asset = null!;
			return false;
		}

		asset = CreateDatabaseEntry(projectRootPath, node);
		return true;
	}

	public bool TryGetPrimaryNodeIdForRelativeSourcePath(string projectRootPath, string relativeSourcePath,
		out Guid nodeId)
	{
		nodeId = Guid.Empty;
		if (_index.TryGetSourceByRelativePath(projectRootPath, relativeSourcePath, out var source) == false)
		{
			return false;
		}

		var nodes = _index.GetNodes(projectRootPath)
			.Where(candidate => candidate.SourceId == source.SourceId)
			.OrderBy(candidate => candidate.NodeKey, StringComparer.Ordinal)
			.ToList();
		var primary =
			nodes.FirstOrDefault(candidate => string.Equals(candidate.NodeKey, "main", StringComparison.Ordinal))
			?? nodes.FirstOrDefault(candidate => string.Equals(candidate.NodeKey, "scene", StringComparison.Ordinal))
			?? nodes.FirstOrDefault();
		if (primary is null)
		{
			return false;
		}

		nodeId = primary.NodeId;
		return true;
	}

	public void AssignSceneCellAssetIds(
		string projectRootPath,
		string relativeScenePath,
		EditorSceneAssetFile sceneAsset,
		string globalCellPath,
		IReadOnlyDictionary<Int2, string> spatialCellPaths)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(relativeScenePath);
		ArgumentNullException.ThrowIfNull(sceneAsset);
		ArgumentException.ThrowIfNullOrWhiteSpace(globalCellPath);
		ArgumentNullException.ThrowIfNull(spatialCellPaths);

		var sceneName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(relativeScenePath));
		sceneAsset.GlobalCellId = AssignSceneCellAssetId(
			projectRootPath,
			globalCellPath,
			EditorSceneAssetFile.GetGlobalCellAssetName(sceneName));

		for (var i = 0; i < sceneAsset.SpatialCells.Count; i++)
		{
			var spatialCell = sceneAsset.SpatialCells[i];
			var coordinates = spatialCell.ToCoordinates();
			if (spatialCellPaths.TryGetValue(coordinates, out var spatialCellPath) == false)
			{
				throw new InvalidOperationException($"Missing save path for scene cell '{coordinates.X}, {coordinates.Y}'.");
			}

			spatialCell.CellId = AssignSceneCellAssetId(
				projectRootPath,
				spatialCellPath,
				EditorSceneAssetFile.GetSpatialCellAssetName(sceneName, coordinates));
		}
	}

	private Guid AssignSceneCellAssetId(string projectRootPath, string relativeCellPath, string assetName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(relativeCellPath);

		InitializeProject(projectRootPath);
		var absoluteCellPath = GetAbsolutePath(projectRootPath, relativeCellPath);
		var absoluteMetaPath = AssetFileExtensions.GetMetaPath(absoluteCellPath);
		var metadata = LoadOrCreateMetadata(absoluteMetaPath, relativeCellPath);
		metadata.ImporterId = AssetImporterIds.SceneCell;
		metadata.ImporterVersion = 1;
		metadata.ImportSettingsJson = "{}";
		var nodeId = GetOrCreateNodeId(metadata, "main", AssetType.SceneCell, assetName);

		_metadataStore.Save(absoluteMetaPath, metadata);
		return nodeId;
	}

	public AssetImportResult ImportExternalSource(string projectRootPath, string absoluteSourcePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(absoluteSourcePath);

		var extension = Path.GetExtension(absoluteSourcePath).ToLowerInvariant();
		var destinationFolder = AssetPipelinePaths.GetAssetsPath(projectRootPath);
		Directory.CreateDirectory(destinationFolder);

		var baseName = Path.GetFileNameWithoutExtension(absoluteSourcePath);
		var destinationPath = GetUniqueDestinationPath(destinationFolder, baseName, extension);
		File.Copy(absoluteSourcePath, destinationPath, overwrite: false);

		var relativePath = ToProjectRelativePath(projectRootPath, destinationPath);
		ImportSource(projectRootPath, destinationPath, relativePath, existingSource: null);
		if (TryGetPrimaryNodeIdForRelativeSourcePath(projectRootPath, relativePath, out var nodeId))
		{
			var source = _index.GetSources(projectRootPath)
				.First(record =>
					string.Equals(record.RelativeSourcePath, relativePath, StringComparison.OrdinalIgnoreCase));
			return new AssetImportResult
			{
				PrimaryNodeId = nodeId,
				PrimarySourceId = source.SourceId
			};
		}

		return new AssetImportResult();
	}

	public void InstantiateImportedModel(string projectRootPath, Guid modelNodeId, World world, Vector3? spawnPosition = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
		ArgumentNullException.ThrowIfNull(world);
		if (_index.TryGetNode(projectRootPath, modelNodeId, out var modelNode) == false)
		{
			throw new InvalidOperationException($"3D model node '{modelNodeId}' was not found.");
		}

		if (modelNode.Type != AssetType.Model3D)
		{
			throw new InvalidOperationException($"Asset node '{modelNodeId}' is not a 3D model.");
		}

		var summary = AssetPipelineSerialization.Deserialize<Model3DAssetSummary>(modelNode.SummaryJson);
		var absoluteModelPath = GetAbsolutePath(projectRootPath, summary.RelativeImportedModelPath);
		var modelFile =
			AssetPipelineSerialization.Deserialize<ImportedModelAssetFile>(File.ReadAllText(absoluteModelPath));
		if (modelFile.Nodes.Count == 0)
		{
			return;
		}

		var rootCount = modelFile.Nodes.Count(node => node.ParentIndex < 0);
		if (rootCount == 0)
		{
			throw new InvalidDataException($"Imported model '{modelNodeId}' does not contain a root node.");
		}

		if (rootCount == 1)
		{
			var rootEntity = CreateModelNodeEntities(modelFile.Nodes, world, rootParent: null, modelFile);
			ApplySpawnPosition(world, rootEntity, spawnPosition);
			return;
		}

		var wrapper =
			world.CreateEntity(string.IsNullOrWhiteSpace(modelFile.Name) ? "Imported 3D Model" : modelFile.Name);
		world.AddTransform(wrapper, System.Numerics.Matrix4x4.Identity);
		CreateModelNodeEntities(modelFile.Nodes, world, wrapper, modelFile);

		ApplySpawnPosition(world, wrapper, spawnPosition);
	}

	public void InstantiatePrefab(string projectRootPath, Guid prefabNodeId, EditorScene scene, Vector3? spawnPosition = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
		ArgumentNullException.ThrowIfNull(scene);
		ArgumentNullException.ThrowIfNull(scene.World);
		if (_index.TryGetNode(projectRootPath, prefabNodeId, out var prefabNode) == false)
		{
			throw new InvalidOperationException($"Prefab node '{prefabNodeId}' was not found.");
		}

		if (prefabNode.Type != AssetType.Prefab)
		{
			throw new InvalidOperationException($"Asset node '{prefabNodeId}' is not a prefab.");
		}

		var prefabFile = PrefabAssetFile.Load(GetAbsolutePath(projectRootPath, prefabNode.RelativeAssetPath));
		if (prefabFile.RootEntityId == Guid.Empty)
		{
			return;
		}

		var entitiesById = prefabFile.Entities.ToDictionary(entity => entity.EntityId);
		var childrenByParent = BuildPrefabChildrenMap(prefabFile.Entities);
		if (entitiesById.TryGetValue(prefabFile.RootEntityId, out var rootEntity) == false)
		{
			throw new InvalidOperationException(
				$"Prefab '{prefabNodeId}' does not contain root entity '{prefabFile.RootEntityId}'.");
		}

		var instantiatedRoot = InstantiatePrefabEntities(
			scene, projectRootPath, prefabNodeId, rootEntity, entitiesById, childrenByParent);
		ApplySpawnPosition(scene.World, instantiatedRoot, spawnPosition);
	}

	private Entity CreateModelNodeEntities(
		IReadOnlyList<ImportedModelAssetNode> nodes,
		World world,
		Entity? rootParent,
		ImportedModelAssetFile? modelFile = null)
	{
		var entities = new Entity[nodes.Count];
		Entity? firstRoot = null;
		var skinnedMeshEntities = new List<Entity>();

		for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
		{
			var node = nodes[nodeIndex];
			var entity = world.CreateEntity(node.Name);
			entities[nodeIndex] = entity;

			if (node.ParentIndex >= 0)
			{
				if (node.ParentIndex >= nodeIndex)
				{
					throw new InvalidDataException(
						$"Imported model node {nodeIndex} has invalid parent index {node.ParentIndex}; parents must precede children.");
				}

				world.SetParent(entity, entities[node.ParentIndex]);
			}
			else
			{
				firstRoot ??= entity;
				if (rootParent is { } parentEntity)
				{
					world.SetParent(entity, parentEntity);
				}
			}

			world.AddTransform(entity, node.LocalTransform);
			for (var meshIndex = 0; meshIndex < node.Meshes.Count; meshIndex++)
			{
				var meshInstance = node.Meshes[meshIndex];
				var meshEntity = node.Meshes.Count == 1 ? entity : world.CreateEntity(meshInstance.Name);
				if (node.Meshes.Count > 1)
				{
					world.SetParent(meshEntity, entity);
					world.AddTransform(meshEntity, System.Numerics.Matrix4x4.Identity);
				}

				var material = AssetDatabase.GetInstance<Material>(meshInstance.MaterialNodeId);
				var mesh = AssetDatabase.GetInstance<Mesh>(meshInstance.MeshNodeId);
				if (material is null || mesh is null)
				{
					continue;
				}

				if (meshInstance.SkeletonNodeId != Guid.Empty && mesh.IsSkinned)
				{
					world.AddComponent(meshEntity, SkinnedMeshRenderer.Create(
						new AssetRef<Mesh> { NodeId = meshInstance.MeshNodeId },
						new AssetRef<Material> { NodeId = meshInstance.MaterialNodeId },
						new AssetRef<Skeleton> { NodeId = meshInstance.SkeletonNodeId },
						// Patched once the animator's owning entity is known, below.
						default));
					skinnedMeshEntities.Add(meshEntity);
					continue;
				}

				world.AddComponent(meshEntity, new MeshRenderer
				{
					MeshAsset = new AssetRef<Mesh> { NodeId = meshInstance.MeshNodeId },
					MaterialAsset = new AssetRef<Material> { NodeId = meshInstance.MaterialNodeId },
					Material = material,
					Mesh = mesh
				});
			}
		}

		var resolvedRoot = firstRoot ?? throw new InvalidDataException("Imported model does not contain a root node.");

		if (skinnedMeshEntities.Count > 0)
		{
			AttachModelAnimator(world, rootParent ?? resolvedRoot, skinnedMeshEntities, modelFile);
		}

		return resolvedRoot;
	}

	/// <summary>
	/// Gives a skinned model one animator that every skinned mesh in it shares. Body and clothing
	/// meshes are separate renderers but a single skeleton, so a per-mesh animator would evaluate
	/// the same pose repeatedly and let the parts drift out of sync.
	/// </summary>
	private static void AttachModelAnimator(
		World world,
		Entity animatorEntity,
		IReadOnlyList<Entity> skinnedMeshEntities,
		ImportedModelAssetFile? modelFile)
	{
		var skeletonNodeId = modelFile?.SkeletonNodeIds.FirstOrDefault() ?? Guid.Empty;
		var clipNodeId = modelFile?.AnimationNodeIds.FirstOrDefault() ?? Guid.Empty;

		if (skeletonNodeId != Guid.Empty && world.HasComponent<Animator>(animatorEntity) == false)
		{
			world.AddComponent(animatorEntity, Animator.Create(
				new AssetRef<Skeleton> { NodeId = skeletonNodeId },
				new AssetRef<AnimationClip> { NodeId = clipNodeId }));
		}

		for (var i = 0; i < skinnedMeshEntities.Count; i++)
		{
			ref var renderer = ref world.GetComponent<SkinnedMeshRenderer>(skinnedMeshEntities[i]);
			renderer.AnimatorEntity = animatorEntity;
		}
	}

	private void ImportSource(string projectRootPath, string absoluteSourcePath, string relativeSourcePath,
		AssetSourceRecord? existingSource)
	{
		var absoluteMetaPath = AssetFileExtensions.GetMetaPath(absoluteSourcePath);
		var relativeMetaPath = AssetFileExtensions.GetRelativeMetaPath(relativeSourcePath);
		var metadata = LoadOrCreateMetadata(absoluteMetaPath, relativeSourcePath);
		ApplyIndexedIdentity(projectRootPath, existingSource, metadata);

		if (TryGetImporterById(metadata.ImporterId, out var importer) == false)
		{
			throw new InvalidOperationException(
				$"Unsupported importer '{metadata.ImporterId}' for '{relativeSourcePath}'.");
		}

		metadata.ImporterVersion = importer.Version;

		var sourceContentHash = AssetHashing.ComputeFileHash(absoluteSourcePath);
		var sourceInfo = new FileInfo(absoluteSourcePath);

		var sourceRecord = new AssetSourceRecord
		{
			SourceId = metadata.SourceId,
			RelativeSourcePath = relativeSourcePath,
			RelativeMetaPath = relativeMetaPath,
			ImporterId = metadata.ImporterId,
			ImporterVersion = metadata.ImporterVersion,
			SourceContentHash = sourceContentHash,
			SourceFileSize = sourceInfo.Length,
			SourceLastWriteTimeUtcTicks = sourceInfo.LastWriteTimeUtc.Ticks,
			ImportSettingsJson = NormalizeImportSettingsJson(metadata)
		};

		LogLibraryBuildStage($"Importing '{relativeSourcePath}' with '{importer.Id}' ({sourceInfo.Length / (1024.0 * 1024.0):F1} MiB).");
		var importGraph = importer.Import(projectRootPath, absoluteSourcePath, relativeSourcePath, relativeMetaPath,
			metadata);
		var activeKeys = importGraph.Nodes.Select(node => node.NodeKey).ToHashSet(StringComparer.Ordinal);
		metadata.SubAssets = metadata.SubAssets
			.Where(entry => activeKeys.Contains(entry.Key))
			.ToList();

		_metadataStore.Save(absoluteMetaPath, metadata);
		_index.UpsertSourceGraph(projectRootPath, sourceRecord, importGraph.Nodes, importGraph.Artifacts,
			importGraph.Dependencies);
		LogLibraryBuildStage($"Finished '{relativeSourcePath}' ({importGraph.Nodes.Count} nodes, {importGraph.Artifacts.Count} artifacts).");
	}

	private void ApplyIndexedIdentity(string projectRootPath, AssetSourceRecord? existingSource,
		AssetSourceMetaFile metadata)
	{
		if (existingSource is null)
		{
			if (metadata.SourceId == Guid.Empty)
			{
				metadata.SourceId = Guid.NewGuid();
			}

			return;
		}

		metadata.SourceId = existingSource.SourceId;
		if (metadata.SubAssets.Count > 0)
		{
			return;
		}

		var indexedNodes = _index.GetNodes(projectRootPath)
			.Where(node => node.SourceId == existingSource.SourceId)
			.OrderBy(node => node.NodeKey, StringComparer.Ordinal)
			.ToList();
		for (var i = 0; i < indexedNodes.Count; i++)
		{
			metadata.SubAssets.Add(new AssetSubAssetManifestEntry
			{
				Key = indexedNodes[i].NodeKey,
				NodeId = indexedNodes[i].NodeId,
				Type = indexedNodes[i].Type,
				Name = indexedNodes[i].Name
			});
		}
	}

	private ImportGraph ImportTextureSource(
		string projectRootPath,
		string absoluteSourcePath,
		string relativeSourcePath,
		string relativeMetaPath,
		AssetSourceMetaFile metadata)
	{
		var importSettings = metadata.GetImportSettingsOrDefault(() => new TextureImportSettings());
		var semantic = importSettings.TextureSemantic;
		LogLibraryBuildStage($"Decoding texture '{relativeSourcePath}' ({semantic}).");
		var importedTexture = _imageLoader.Load(absoluteSourcePath, semantic);
		LogLibraryBuildStage($"Decoded texture '{relativeSourcePath}' ({importedTexture.Width}x{importedTexture.Height}).");
		var nodeId = GetOrCreateNodeId(metadata, "main", AssetType.Texture2D,
			Path.GetFileNameWithoutExtension(relativeSourcePath));
		var relativeImportedPath = NormalizeRelativePath(Path.Combine(
			AssetPipelinePaths.LibraryFolderName,
			AssetPipelinePaths.ImportedFolderName,
			metadata.SourceId.ToString("D"),
			"texture.bin"));
		var runtimeArtifacts = WriteTextureArtifacts(projectRootPath, nodeId, relativeImportedPath, importedTexture);

		var summary = new TextureAssetSummary
		{
			RelativeSourceAssetPath = relativeSourcePath,
			RelativeImportedPath = relativeImportedPath,
			RelativeRuntimeArtifactPath = string.Empty,
			Width = importedTexture.Width,
			Height = importedTexture.Height,
			Channels = importedTexture.Channels,
			Semantic = importedTexture.Semantic,
			SourceExtension = Path.GetExtension(relativeSourcePath).ToLowerInvariant()
		};

		return new ImportGraph
		{
			Nodes =
			[
				new AssetNodeRecord
				{
					NodeId = nodeId,
					SourceId = metadata.SourceId,
					Type = AssetType.Texture2D,
					NodeKey = "main",
					Name = Path.GetFileNameWithoutExtension(relativeSourcePath),
					IsGenerated = false,
					RelativeSourcePath = relativeSourcePath,
					RelativeAssetPath = relativeSourcePath,
					RelativeMetaPath = relativeMetaPath,
					SummaryJson = AssetPipelineSerialization.Serialize(summary)
				}
			],
			Artifacts = runtimeArtifacts,
			Dependencies = []
		};
	}

	private ImportGraph ImportAudioSource(
		string projectRootPath,
		string absoluteSourcePath,
		string relativeSourcePath,
		string relativeMetaPath,
		AssetSourceMetaFile metadata)
	{
		var settings = metadata.GetImportSettingsOrDefault(() => new AudioImportSettings());
		var nodeId = GetOrCreateNodeId(metadata, "main", AssetType.AudioClip,
			Path.GetFileNameWithoutExtension(relativeSourcePath));
		var relativeArtifactPath = NormalizeRelativePath(Path.Combine(
			AssetPipelinePaths.LibraryFolderName,
			AssetPipelinePaths.ArtifactsFolderName,
			nodeId.ToString("D"),
			"audio" + AudioAssetConstants.RuntimeArtifactExtension));
		var absoluteArtifactPath = GetAbsolutePath(projectRootPath, relativeArtifactPath);
		Directory.CreateDirectory(Path.GetDirectoryName(absoluteArtifactPath)!);
		var result = AudioCooker.Cook(absoluteSourcePath, absoluteArtifactPath, settings);
		var info = new FileInfo(absoluteArtifactPath);
		return new ImportGraph
		{
			Nodes =
			[
				new AssetNodeRecord
				{
					NodeId = nodeId,
					SourceId = metadata.SourceId,
					Type = AssetType.AudioClip,
					NodeKey = "main",
					Name = Path.GetFileNameWithoutExtension(relativeSourcePath),
					IsGenerated = false,
					RelativeSourcePath = relativeSourcePath,
					RelativeAssetPath = relativeSourcePath,
					RelativeMetaPath = relativeMetaPath,
					SummaryJson = AssetPipelineSerialization.Serialize(result.Summary)
				}
			],
			Artifacts =
			[
				new AssetArtifactRecord
				{
					NodeId = nodeId,
					ArtifactKey = "runtime-audio",
					Kind = AudioAssetConstants.RuntimeArtifactKind,
					Target = AudioAssetConstants.RuntimeArtifactTarget,
					RelativePath = relativeArtifactPath,
					ContentHash = AssetHashing.ComputeFileHash(absoluteArtifactPath),
					ByteSize = info.Length,
					ChunkIndex = 0,
					ChunkCount = 1,
					StreamGroup = result.Summary.StorageMode == AudioStorageMode.Streaming ? "audio-streaming" : "audio-sfx",
					MetadataJson = AssetPipelineSerialization.Serialize(result.Header)
				}
			],
			Dependencies = []
		};
	}

	private ImportGraph ImportMaterialSource(
		string projectRootPath,
		string absoluteSourcePath,
		string relativeSourcePath,
		string relativeMetaPath,
		AssetSourceMetaFile metadata)
	{
		var materialAsset = _materialAssetStore.LoadAsset(absoluteSourcePath);
		var nodeId = GetOrCreateNodeId(metadata, "main", AssetType.Material,
			Path.GetFileNameWithoutExtension(relativeSourcePath));
		var summary = new MaterialAssetSummary
		{
			MaterialType = materialAsset.MaterialType
		};

		var dependencies = CreateMaterialDependencies(materialAsset);
		return new ImportGraph
		{
			Nodes =
			[
				new AssetNodeRecord
				{
					NodeId = nodeId,
					SourceId = metadata.SourceId,
					Type = AssetType.Material,
					NodeKey = "main",
					Name = Path.GetFileNameWithoutExtension(relativeSourcePath),
					IsGenerated = false,
					RelativeSourcePath = relativeSourcePath,
					RelativeAssetPath = relativeSourcePath,
					RelativeMetaPath = relativeMetaPath,
					SummaryJson = AssetPipelineSerialization.Serialize(summary)
				}
			],
			Artifacts = [],
			Dependencies = dependencies.Select(dependency => new AssetDependencyRecord
			{
				FromNodeId = nodeId,
				ToNodeId = dependency.TargetNodeId,
				Kind = dependency.Kind,
				IsHard = true
			}).ToList()
		};
	}

	private ImportGraph ImportDataAssetSource(
		string projectRootPath,
		string absoluteSourcePath,
		string relativeSourcePath,
		string relativeMetaPath,
		AssetSourceMetaFile metadata)
	{
		var loadResult = _dataAssetStore.LoadAsset(absoluteSourcePath);
		var nodeId = GetOrCreateNodeId(metadata, "main", AssetType.DataAsset,
			Path.GetFileNameWithoutExtension(relativeSourcePath));
		var summary = new DataAssetSummary
		{
			DataAssetType = loadResult.DataAssetType.AssemblyQualifiedName ??
			                loadResult.DataAssetType.FullName ?? loadResult.DataAssetType.Name,
			DataAssetTypeId = loadResult.DataAssetTypeId,
			DisplayName = loadResult.DataAssetType.Name
		};

		return new ImportGraph
		{
			Nodes =
			[
				new AssetNodeRecord
				{
					NodeId = nodeId,
					SourceId = metadata.SourceId,
					Type = AssetType.DataAsset,
					NodeKey = "main",
					Name = Path.GetFileNameWithoutExtension(relativeSourcePath),
					IsGenerated = false,
					RelativeSourcePath = relativeSourcePath,
					RelativeAssetPath = relativeSourcePath,
					RelativeMetaPath = relativeMetaPath,
					SummaryJson = AssetPipelineSerialization.Serialize(summary)
				}
			],
			Artifacts = [],
			Dependencies = []
		};
	}

	private ImportGraph ImportTerrainSource(
		string absoluteSourcePath,
		string relativeSourcePath,
		string relativeMetaPath,
		AssetSourceMetaFile metadata)
	{
		var terrainAsset =
			TerrainAssetSerializer.Read(absoluteSourcePath, Path.GetFileNameWithoutExtension(relativeSourcePath));
		var nodeId = GetOrCreateNodeId(metadata, "main", AssetType.Terrain,
			Path.GetFileNameWithoutExtension(relativeSourcePath));
		var summary = new TerrainAssetSummary
		{
			HeightmapWidth = terrainAsset.HeightmapWidth,
			HeightmapHeight = terrainAsset.HeightmapHeight,
			LayerMapWidth = terrainAsset.LayerMapWidth,
			LayerMapHeight = terrainAsset.LayerMapHeight,
			LayerMipCount = terrainAsset.LayerIndexMap.MipCount
		};

		return new ImportGraph
		{
			Nodes =
			[
				new AssetNodeRecord
				{
					NodeId = nodeId,
					SourceId = metadata.SourceId,
					Type = AssetType.Terrain,
					NodeKey = "main",
					Name = Path.GetFileNameWithoutExtension(relativeSourcePath),
					IsGenerated = false,
					RelativeSourcePath = relativeSourcePath,
					RelativeAssetPath = relativeSourcePath,
					RelativeMetaPath = relativeMetaPath,
					SummaryJson = AssetPipelineSerialization.Serialize(summary)
				}
			],
			Artifacts = [],
			Dependencies = []
		};
	}

	private ImportGraph ImportThreeDSource(
		string projectRootPath,
		string absoluteSourcePath,
		string relativeSourcePath,
		string relativeMetaPath,
		AssetSourceMetaFile metadata)
	{
		var importSettings = metadata.GetImportSettingsOrDefault(() => new ModelImportSettings());
		LogLibraryBuildStage($"Parsing 3D source '{relativeSourcePath}' (scale {importSettings.GetEffectiveScaleFactor()}).");
		var importedScene = _threeDFileImporter.Import(absoluteSourcePath, importSettings);
		LogLibraryBuildStage($"Parsed 3D source '{relativeSourcePath}' ({importedScene.Textures.Count} textures, {importedScene.Materials.Count} materials, {importedScene.Nodes.Count(node => node.ParentIndex < 0)} root nodes).");
		var nodes = new List<AssetNodeRecord>();
		var artifacts = new List<AssetArtifactRecord>();
		var dependencies = new List<AssetDependencyRecord>();

		var textureNodeIds = new List<Guid>(importedScene.Textures.Count);
		for (var i = 0; i < importedScene.Textures.Count; i++)
		{
			var importedTexture = importedScene.Textures[i];
			var nodeKey = $"texture:{i}";
			var name = string.IsNullOrWhiteSpace(importedTexture.NameOrPath)
				? $"Texture {i}"
				: Path.GetFileNameWithoutExtension(importedTexture.NameOrPath);
			var nodeId = GetOrCreateNodeId(metadata, nodeKey, AssetType.Texture2D, name);
			textureNodeIds.Add(nodeId);

			var relativeImportedPath = NormalizeRelativePath(Path.Combine(
				AssetPipelinePaths.LibraryFolderName,
				AssetPipelinePaths.ImportedFolderName,
				metadata.SourceId.ToString("D"),
				"textures",
				$"{nodeKey.Replace(':', '_')}.bin"));
			var runtimeArtifacts =
				WriteTextureArtifacts(projectRootPath, nodeId, relativeImportedPath, importedTexture);

			nodes.Add(new AssetNodeRecord
			{
				NodeId = nodeId,
				SourceId = metadata.SourceId,
				Type = AssetType.Texture2D,
				NodeKey = nodeKey,
				Name = name,
				IsGenerated = true,
				RelativeSourcePath = relativeSourcePath,
				RelativeAssetPath = string.Empty,
				RelativeMetaPath = relativeMetaPath,
				SummaryJson = AssetPipelineSerialization.Serialize(new TextureAssetSummary
				{
					RelativeSourceAssetPath = string.Empty,
					RelativeImportedPath = relativeImportedPath,
					RelativeRuntimeArtifactPath = string.Empty,
					Width = importedTexture.Width,
					Height = importedTexture.Height,
					Channels = importedTexture.Channels,
					Semantic = importedTexture.Semantic,
					SourceExtension = Path.GetExtension(importedTexture.NameOrPath ?? string.Empty).ToLowerInvariant()
				})
			});
			artifacts.AddRange(runtimeArtifacts);
		}

		var materialNodeIds = new List<Guid>(importedScene.Materials.Count);
		for (var i = 0; i < importedScene.Materials.Count; i++)
		{
			var importedMaterial = importedScene.Materials[i];
			var ormTextureIndex = EnsureOrmTexture(
				projectRootPath,
				metadata,
				relativeSourcePath,
				relativeMetaPath,
				importedScene.Textures,
				textureNodeIds,
				nodes,
				artifacts,
				importedMaterial,
				i);
			var nodeKey = $"material:{i}";
			var name = $"Material {i}";
			var nodeId = GetOrCreateNodeId(metadata, nodeKey, AssetType.Material, name);
			materialNodeIds.Add(nodeId);

			var materialAsset = CreateGeneratedMaterialAsset(importedMaterial, textureNodeIds, ormTextureIndex);
			var relativeMaterialPath = NormalizeRelativePath(Path.Combine(
				AssetPipelinePaths.LibraryFolderName,
				AssetPipelinePaths.ImportedFolderName,
				metadata.SourceId.ToString("D"),
				"materials",
				$"{nodeKey.Replace(':', '_')}.mat.json"));
			_materialAssetStore.SaveAsset(GetAbsolutePath(projectRootPath, relativeMaterialPath), materialAsset);

			nodes.Add(new AssetNodeRecord
			{
				NodeId = nodeId,
				SourceId = metadata.SourceId,
				Type = AssetType.Material,
				NodeKey = nodeKey,
				Name = name,
				IsGenerated = true,
				RelativeSourcePath = relativeSourcePath,
				RelativeAssetPath = relativeMaterialPath,
				RelativeMetaPath = relativeMetaPath,
				SummaryJson = AssetPipelineSerialization.Serialize(new MaterialAssetSummary
				{
					MaterialType = materialAsset.MaterialType
				})
			});

			foreach (var dependency in CreateMaterialDependencies(materialAsset))
			{
				dependencies.Add(new AssetDependencyRecord
				{
					FromNodeId = nodeId,
					ToNodeId = dependency.TargetNodeId,
					Kind = dependency.Kind,
					IsHard = true
				});
			}
		}

		var skeletonNodeIds = WriteSkeletonSubAssets(
			projectRootPath,
			metadata,
			relativeSourcePath,
			relativeMetaPath,
			importedScene.Skeletons,
			nodes);

		var animationNodeIds = WriteAnimationSubAssets(
			projectRootPath,
			metadata,
			relativeSourcePath,
			relativeMetaPath,
			importedScene,
			skeletonNodeIds,
			nodes,
			dependencies);

		var sourceAssetName = Path.GetFileName(relativeSourcePath);
		var modelDisplayName = Path.GetFileNameWithoutExtension(relativeSourcePath);
		var totalRootCount = importedScene.Nodes.Count(node => node.ParentIndex < 0);
		var modelGraph = new ImportedModelAssetFile
		{
			Name = modelDisplayName,
			Nodes = new List<ImportedModelAssetNode>(importedScene.Nodes.Count),
			SkeletonNodeIds = skeletonNodeIds,
			AnimationNodeIds = animationNodeIds
		};
		var hierarchyKeys = new string[importedScene.Nodes.Count];
		var childCounts = new int[importedScene.Nodes.Count];
		var rootCount = 0;
		for (var i = 0; i < importedScene.Nodes.Count; i++)
		{
			var importedNode = importedScene.Nodes[i];
			string hierarchyKey;
			if (importedNode.ParentIndex < 0)
			{
				hierarchyKey = $"root-{rootCount++}-{SanitizeKey(importedNode.Name)}";
			}
			else
			{
				if (importedNode.ParentIndex >= i)
				{
					throw new InvalidDataException(
						$"Imported node {i} has invalid parent index {importedNode.ParentIndex}; parents must precede children.");
				}

				var childIndex = childCounts[importedNode.ParentIndex]++;
				hierarchyKey =
					$"{hierarchyKeys[importedNode.ParentIndex]}/child-{childIndex}-{SanitizeKey(importedNode.Name)}";
			}

			hierarchyKeys[i] = hierarchyKey;
			modelGraph.Nodes.Add(CreateModelNode(
				projectRootPath,
				metadata,
				relativeSourcePath,
				relativeMetaPath,
				hierarchyKey,
				importedNode,
				GetModelNodeDisplayName(importedNode, totalRootCount, modelDisplayName),
				materialNodeIds,
				skeletonNodeIds,
				nodes,
				dependencies));
		}

		var modelNodeId = GetOrCreateNodeId(metadata, "scene", AssetType.Model3D, sourceAssetName);
		var relativeModelPath = NormalizeRelativePath(Path.Combine(
			AssetPipelinePaths.LibraryFolderName,
			AssetPipelinePaths.ImportedFolderName,
			metadata.SourceId.ToString("D"),
			"model3d.asset.json"));
		WriteJsonFile(GetAbsolutePath(projectRootPath, relativeModelPath), modelGraph);
		nodes.Add(new AssetNodeRecord
		{
			NodeId = modelNodeId,
			SourceId = metadata.SourceId,
			Type = AssetType.Model3D,
			NodeKey = "scene",
			Name = sourceAssetName,
			IsGenerated = true,
			RelativeSourcePath = relativeSourcePath,
			RelativeAssetPath = relativeModelPath,
			RelativeMetaPath = relativeMetaPath,
			SummaryJson = AssetPipelineSerialization.Serialize(new Model3DAssetSummary
			{
				RelativeImportedModelPath = relativeModelPath,
				RootNodeCount = rootCount,
				SkeletonCount = skeletonNodeIds.Count,
				AnimationCount = animationNodeIds.Count
			})
		});

		var emittedModelMaterialDependencies = new HashSet<Guid>();
		for (var i = 0; i < modelGraph.Nodes.Count; i++)
		{
			AddModelDependencies(modelNodeId, modelGraph.Nodes[i], dependencies, emittedModelMaterialDependencies);
		}

		return new ImportGraph
		{
			Nodes = nodes,
			Artifacts = artifacts,
			Dependencies = dependencies
		};
	}

	private int? EnsureOrmTexture(
		string projectRootPath,
		AssetSourceMetaFile metadata,
		string relativeSourcePath,
		string relativeMetaPath,
		List<ImportedTexture> textures,
		List<Guid> textureNodeIds,
		List<AssetNodeRecord> nodes,
		List<AssetArtifactRecord> artifacts,
		ImportedMaterial importedMaterial,
		int materialIndex)
	{
		if (importedMaterial.MetallicRoughnessTextureIndex is not { } metallicRoughnessIndex &&
		    importedMaterial.OcclusionTextureIndex is not { })
		{
			return null;
		}

		var metallicRoughnessTexture = ResolveImportedTexture(importedMaterial.MetallicRoughnessTextureIndex, textures);
		var occlusionTexture = ResolveImportedTexture(importedMaterial.OcclusionTextureIndex, textures);
		var ormTexture = CreateOrmImportedTexture(metallicRoughnessTexture, occlusionTexture, materialIndex);
		var ormTextureIndex = textures.Count;
		textures.Add(ormTexture);

		var nodeKey = $"texture:orm:{materialIndex}";
		var name = string.IsNullOrWhiteSpace(ormTexture.NameOrPath)
			? $"ORM {materialIndex}"
			: Path.GetFileNameWithoutExtension(ormTexture.NameOrPath);
		var nodeId = GetOrCreateNodeId(metadata, nodeKey, AssetType.Texture2D, name);
		textureNodeIds.Add(nodeId);

		var relativeImportedPath = NormalizeRelativePath(Path.Combine(
			AssetPipelinePaths.LibraryFolderName,
			AssetPipelinePaths.ImportedFolderName,
			metadata.SourceId.ToString("D"),
			"textures",
			$"{nodeKey.Replace(':', '_')}.bin"));
		var runtimeArtifacts = WriteTextureArtifacts(projectRootPath, nodeId, relativeImportedPath, ormTexture);

		nodes.Add(new AssetNodeRecord
		{
			NodeId = nodeId,
			SourceId = metadata.SourceId,
			Type = AssetType.Texture2D,
			NodeKey = nodeKey,
			Name = name,
			IsGenerated = true,
			RelativeSourcePath = relativeSourcePath,
			RelativeAssetPath = string.Empty,
			RelativeMetaPath = relativeMetaPath,
			SummaryJson = AssetPipelineSerialization.Serialize(new TextureAssetSummary
			{
				RelativeSourceAssetPath = string.Empty,
				RelativeImportedPath = relativeImportedPath,
				RelativeRuntimeArtifactPath = string.Empty,
				Width = ormTexture.Width,
				Height = ormTexture.Height,
				Channels = ormTexture.Channels,
				Semantic = ormTexture.Semantic,
				SourceExtension = Path.GetExtension(ormTexture.NameOrPath ?? string.Empty).ToLowerInvariant()
			})
		});
		artifacts.AddRange(runtimeArtifacts);
		return ormTextureIndex;
	}

	private ImportGraph ImportEditorSceneSource(
		string absoluteSourcePath,
		string relativeSourcePath,
		string relativeMetaPath,
		AssetSourceMetaFile metadata)
	{
		var sceneAsset = EditorSceneAssetFile.Load(absoluteSourcePath);
		var assetName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(relativeSourcePath));
		var sceneNodeId = GetOrCreateNodeId(metadata, "main", AssetType.Scene, assetName);

		var nodes = new List<AssetNodeRecord>
		{
			new()
			{
				NodeId = sceneNodeId,
				SourceId = metadata.SourceId,
				Type = AssetType.Scene,
				NodeKey = "main",
				Name = assetName,
				IsGenerated = false,
				RelativeSourcePath = relativeSourcePath,
				RelativeAssetPath = relativeSourcePath,
				RelativeMetaPath = relativeMetaPath,
				SummaryJson = AssetPipelineSerialization.Serialize(new SceneAssetSummary
				{
					GlobalCellId = sceneAsset.GlobalCellId,
					SpatialCellCount = sceneAsset.SpatialCells.Count
				})
			}
		};
		var dependencies = new List<AssetDependencyRecord>();
		AddSceneCellDependency(sceneAsset.GlobalCellId);
		for (var i = 0; i < sceneAsset.SpatialCells.Count; i++)
		{
			AddSceneCellDependency(sceneAsset.SpatialCells[i].CellId);
		}

		return new ImportGraph
		{
			Nodes = nodes,
			Artifacts = [],
			Dependencies = dependencies
		};

		void AddSceneCellDependency(Guid cellId)
		{
			if (cellId == Guid.Empty)
			{
				return;
			}

			dependencies.Add(new AssetDependencyRecord
			{
				FromNodeId = sceneNodeId,
				ToNodeId = cellId,
				Kind = "scene-cell",
				IsHard = true
			});
		}
	}

	private ImportGraph ImportSceneCellSource(
		string projectRootPath,
		string absoluteSourcePath,
		string relativeSourcePath,
		string relativeMetaPath,
		AssetSourceMetaFile metadata)
	{
		_ = projectRootPath;
		_ = absoluteSourcePath;
		var assetName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(relativeSourcePath));
		var nodeId = GetOrCreateNodeId(metadata, "main", AssetType.SceneCell, assetName);
		return new ImportGraph
		{
			Nodes =
			[
				new AssetNodeRecord
				{
					NodeId = nodeId,
					SourceId = metadata.SourceId,
					Type = AssetType.SceneCell,
					NodeKey = "main",
					Name = assetName,
					IsGenerated = false,
					RelativeSourcePath = relativeSourcePath,
					RelativeAssetPath = relativeSourcePath,
					RelativeMetaPath = relativeMetaPath,
					SummaryJson = AssetPipelineSerialization.Serialize(new SceneCellAssetSummary
					{
						RelativeCellPath = relativeSourcePath,
						IsGlobal = string.Equals(Path.GetFileName(relativeSourcePath), $"global{Cell.FileExtension}", StringComparison.OrdinalIgnoreCase)
					})
				}
			],
			Artifacts = [],
			Dependencies = []
		};
	}

	/// <summary>
	/// Writes one skeleton artifact per imported skeleton and registers it as a sub-asset of the
	/// source, the same way meshes and materials are registered.
	/// </summary>
	private List<Guid> WriteSkeletonSubAssets(
		string projectRootPath,
		AssetSourceMetaFile metadata,
		string relativeSourcePath,
		string relativeMetaPath,
		IReadOnlyList<ImportedSkeleton> skeletons,
		List<AssetNodeRecord> nodes)
	{
		var skeletonNodeIds = new List<Guid>(skeletons.Count);
		for (var i = 0; i < skeletons.Count; i++)
		{
			var skeleton = skeletons[i];
			var nodeKey = $"skeleton:{i}";
			var name = string.IsNullOrWhiteSpace(skeleton.Name) ? $"Skeleton {i}" : skeleton.Name;
			var nodeId = GetOrCreateNodeId(metadata, nodeKey, AssetType.Skeleton, name);
			skeletonNodeIds.Add(nodeId);

			var relativeSkeletonPath = NormalizeRelativePath(Path.Combine(
				AssetPipelinePaths.LibraryFolderName,
				AssetPipelinePaths.ImportedFolderName,
				metadata.SourceId.ToString("D"),
				"skeletons",
				$"{nodeKey.Replace(':', '_')}.skel.bin"));

			SkeletonSerializer.Write(GetAbsolutePath(projectRootPath, relativeSkeletonPath), new ImportedSkeletonAssetFile
			{
				Name = skeleton.Name,
				BoneNames = skeleton.BoneNames,
				ParentIndices = skeleton.ParentIndices,
				BindPoseLocal = skeleton.BindPoseLocal,
				InverseBindMatrices = skeleton.InverseBindMatrices
			});

			nodes.Add(new AssetNodeRecord
			{
				NodeId = nodeId,
				SourceId = metadata.SourceId,
				Type = AssetType.Skeleton,
				NodeKey = nodeKey,
				Name = name,
				IsGenerated = true,
				RelativeSourcePath = relativeSourcePath,
				RelativeAssetPath = relativeSkeletonPath,
				RelativeMetaPath = relativeMetaPath,
				SummaryJson = AssetPipelineSerialization.Serialize(new SkeletonAssetSummary
				{
					RelativeImportedSkeletonPath = relativeSkeletonPath,
					BoneCount = skeleton.BoneNames.Length,
					RootBoneName = skeleton.BoneNames.Length > 0 ? skeleton.BoneNames[0] : string.Empty
				})
			});
		}

		return skeletonNodeIds;
	}

	private List<Guid> WriteAnimationSubAssets(
		string projectRootPath,
		AssetSourceMetaFile metadata,
		string relativeSourcePath,
		string relativeMetaPath,
		ImportedScene importedScene,
		IReadOnlyList<Guid> skeletonNodeIds,
		List<AssetNodeRecord> nodes,
		List<AssetDependencyRecord> dependencies)
	{
		var animationNodeIds = new List<Guid>(importedScene.Animations.Count);
		for (var i = 0; i < importedScene.Animations.Count; i++)
		{
			var animation = importedScene.Animations[i];
			var nodeKey = $"animation:{i}";
			var name = string.IsNullOrWhiteSpace(animation.Name) ? $"Animation {i}" : animation.Name;
			var nodeId = GetOrCreateNodeId(metadata, nodeKey, AssetType.AnimationClip, name);
			animationNodeIds.Add(nodeId);

			var relativeClipPath = NormalizeRelativePath(Path.Combine(
				AssetPipelinePaths.LibraryFolderName,
				AssetPipelinePaths.ImportedFolderName,
				metadata.SourceId.ToString("D"),
				"animations",
				$"{nodeKey.Replace(':', '_')}.anim.bin"));

			var sourceSkeleton = animation.SkeletonIndex >= 0 && animation.SkeletonIndex < importedScene.Skeletons.Count
				? importedScene.Skeletons[animation.SkeletonIndex]
				: null;

			AnimationClipSerializer.Write(GetAbsolutePath(projectRootPath, relativeClipPath), new ImportedAnimationClipAssetFile
			{
				Name = animation.Name,
				Duration = animation.Duration,
				FramesPerSecond = animation.FramesPerSecond,
				Loop = true,
				TransformTracks = animation.TransformTracks,
				PropertyTracks = animation.PropertyTracks,
				// Retargeting needs the rest pose the clip was authored against, so it travels with
				// the clip rather than being looked up from whatever skeleton happens to play it.
				SourceSkeletonName = sourceSkeleton?.Name ?? string.Empty,
				SourceBindPoseLocal = sourceSkeleton?.BindPoseLocal ?? []
			});

			nodes.Add(new AssetNodeRecord
			{
				NodeId = nodeId,
				SourceId = metadata.SourceId,
				Type = AssetType.AnimationClip,
				NodeKey = nodeKey,
				Name = name,
				IsGenerated = true,
				RelativeSourcePath = relativeSourcePath,
				RelativeAssetPath = relativeClipPath,
				RelativeMetaPath = relativeMetaPath,
				SummaryJson = AssetPipelineSerialization.Serialize(new AnimationClipAssetSummary
				{
					RelativeImportedClipPath = relativeClipPath,
					Duration = animation.Duration,
					FramesPerSecond = animation.FramesPerSecond,
					TransformTrackCount = animation.TransformTracks.Length,
					PropertyTrackCount = animation.PropertyTracks.Length
				})
			});

			if (animation.SkeletonIndex >= 0 && animation.SkeletonIndex < skeletonNodeIds.Count)
			{
				dependencies.Add(new AssetDependencyRecord
				{
					FromNodeId = nodeId,
					ToNodeId = skeletonNodeIds[animation.SkeletonIndex],
					Kind = "animation-skeleton",
					IsHard = false
				});
			}
		}

		return animationNodeIds;
	}

	private static string GetModelNodeDisplayName(ImportedNode node, int totalRootCount, string modelDisplayName)
	{
		if (node.ParentIndex >= 0 || totalRootCount != 1 || string.IsNullOrWhiteSpace(modelDisplayName))
		{
			return node.Name;
		}

		return modelDisplayName;
	}

	private ImportedModelAssetNode CreateModelNode(
		string projectRootPath,
		AssetSourceMetaFile metadata,
		string relativeSourcePath,
		string relativeMetaPath,
		string hierarchyKey,
		ImportedNode node,
		string displayName,
		IReadOnlyList<Guid> materialNodeIds,
		IReadOnlyList<Guid> skeletonNodeIds,
		List<AssetNodeRecord> nodes,
		List<AssetDependencyRecord> dependencies)
	{
		var modelNode = new ImportedModelAssetNode
		{
			Name = displayName,
			LocalTransform = node.LocalTransform,
			ParentIndex = node.ParentIndex
		};

		for (var i = 0; i < node.Meshes.Count; i++)
		{
			var meshInfo = node.Meshes[i];
			var nodeKey = $"mesh:{hierarchyKey}:{i}:{SanitizeKey(meshInfo.Name)}";
			var meshNodeId = GetOrCreateNodeId(metadata, nodeKey, AssetType.Mesh, meshInfo.Name);
			var relativeMeshPath = NormalizeRelativePath(Path.Combine(
				AssetPipelinePaths.LibraryFolderName,
				AssetPipelinePaths.ImportedFolderName,
				metadata.SourceId.ToString("D"),
				"meshes",
				$"{nodeKey.Replace(':', '_')}.mesh.bin"));
			ImportedMeshSerializer.Write(GetAbsolutePath(projectRootPath, relativeMeshPath), new ImportedMeshAssetFile
			{
				Vertices = meshInfo.Mesh.Vertices,
				Indices = meshInfo.Mesh.Indices,
				Normals = meshInfo.Mesh.Normals,
				Tangents = meshInfo.Mesh.Tangents,
				UVs = meshInfo.Mesh.UVs,
				BoneIndices = meshInfo.Mesh.BoneIndices ?? [],
				BoneWeights = meshInfo.Mesh.BoneWeights ?? []
			});
			nodes.Add(new AssetNodeRecord
			{
				NodeId = meshNodeId,
				SourceId = metadata.SourceId,
				Type = AssetType.Mesh,
				NodeKey = nodeKey,
				Name = string.IsNullOrWhiteSpace(meshInfo.Name) ? "Mesh" : meshInfo.Name,
				IsGenerated = true,
				RelativeSourcePath = relativeSourcePath,
				RelativeAssetPath = relativeMeshPath,
				RelativeMetaPath = relativeMetaPath,
				SummaryJson = AssetPipelineSerialization.Serialize(new MeshAssetSummary
				{
					RelativeImportedMeshPath = relativeMeshPath,
					VertexCount = meshInfo.Mesh.Vertices.Length,
					IndexCount = meshInfo.Mesh.Indices.Length,
					IsSkinned = meshInfo.Mesh.IsSkinned
				})
			});

			var materialNodeId = meshInfo.MaterialIndex >= 0 && meshInfo.MaterialIndex < materialNodeIds.Count
				? materialNodeIds[meshInfo.MaterialIndex]
				: Guid.Empty;
			var skeletonNodeId = meshInfo.SkeletonIndex >= 0 && meshInfo.SkeletonIndex < skeletonNodeIds.Count
				? skeletonNodeIds[meshInfo.SkeletonIndex]
				: Guid.Empty;
			modelNode.Meshes.Add(new ImportedModelAssetMeshInstance
			{
				Name = meshInfo.Name,
				MeshNodeId = meshNodeId,
				MaterialNodeId = materialNodeId,
				SkeletonNodeId = skeletonNodeId
			});
			if (materialNodeId != Guid.Empty)
			{
				dependencies.Add(new AssetDependencyRecord
				{
					FromNodeId = meshNodeId,
					ToNodeId = materialNodeId,
					Kind = "mesh-material",
					IsHard = true
				});
			}

			if (skeletonNodeId != Guid.Empty)
			{
				dependencies.Add(new AssetDependencyRecord
				{
					FromNodeId = meshNodeId,
					ToNodeId = skeletonNodeId,
					Kind = "mesh-skeleton",
					IsHard = true
				});
			}
		}

		return modelNode;
	}

	private ImportGraph ImportPrefabSource(
		string absoluteSourcePath,
		string relativeSourcePath,
		string relativeMetaPath,
		AssetSourceMetaFile metadata)
	{
		var prefabAsset = PrefabAssetFile.Load(absoluteSourcePath);
		var assetName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(relativeSourcePath));

		return new ImportGraph
		{
			Nodes =
			[
				new AssetNodeRecord
				{
					NodeId = GetOrCreateNodeId(metadata, "main", AssetType.Prefab, assetName),
					SourceId = metadata.SourceId,
					Type = AssetType.Prefab,
					NodeKey = "main",
					Name = assetName,
					IsGenerated = false,
					RelativeSourcePath = relativeSourcePath,
					RelativeAssetPath = relativeSourcePath,
					RelativeMetaPath = relativeMetaPath,
					SummaryJson = AssetPipelineSerialization.Serialize(new PrefabAssetSummary
					{
						RootEntityId = prefabAsset.RootEntityId,
						EntityCount = prefabAsset.Entities.Count
					})
				}
			],
			Artifacts = [],
			Dependencies = []
		};
	}

	private static void AddModelDependencies(
		Guid modelNodeId,
		ImportedModelAssetNode node,
		List<AssetDependencyRecord> dependencies,
		HashSet<Guid> emittedModelMaterialDependencies)
	{
		for (var i = 0; i < node.Meshes.Count; i++)
		{
			var mesh = node.Meshes[i];
			dependencies.Add(new AssetDependencyRecord
			{
				FromNodeId = modelNodeId,
				ToNodeId = mesh.MeshNodeId,
				Kind = "model-mesh",
				IsHard = true
			});
			if (mesh.MaterialNodeId != Guid.Empty && emittedModelMaterialDependencies.Add(mesh.MaterialNodeId))
			{
				dependencies.Add(new AssetDependencyRecord
				{
					FromNodeId = modelNodeId,
					ToNodeId = mesh.MaterialNodeId,
					Kind = "model-material",
					IsHard = true
				});
			}
		}

	}

	private static string NormalizeImportSettingsJson(AssetSourceMetaFile metadata)
	{
		if (string.Equals(metadata.ImporterId, AssetImporterIds.Texture, StringComparison.Ordinal))
		{
			metadata.GetImportSettingsOrDefault(() => new TextureImportSettings());
		}
		else if (string.Equals(metadata.ImporterId, AssetImporterIds.Audio, StringComparison.Ordinal))
		{
			metadata.GetImportSettingsOrDefault(() => new AudioImportSettings());
		}
		else if (string.Equals(metadata.ImporterId, AssetImporterIds.ThreeDScene, StringComparison.Ordinal))
		{
			metadata.GetImportSettingsOrDefault(() => new ModelImportSettings());
		}

		return string.IsNullOrWhiteSpace(metadata.ImportSettingsJson) ? "{}" : metadata.ImportSettingsJson;
	}

	private bool TryRefreshSourceScanState(
		string projectRootPath,
		string absoluteSourcePath,
		string relativeSourcePath,
		AssetSourceRecord existingSource)
	{
		if (TryLoadUsableMetadata(AssetFileExtensions.GetMetaPath(absoluteSourcePath), relativeSourcePath,
			    out var metadata) == false)
		{
			return false;
		}

		if (TryGetImporterForPath(relativeSourcePath, out var importer) == false
		    || metadata.ImporterVersion != importer.Version)
		{
			return false;
		}

		var importSettingsJson = NormalizeImportSettingsJson(metadata);
		var sourceInfo = new FileInfo(absoluteSourcePath);
		var relativeMetaPath = AssetFileExtensions.GetRelativeMetaPath(relativeSourcePath);
		var importerVersionChanged = metadata.ImporterVersion != existingSource.ImporterVersion;
		var importerChanged = string.Equals(metadata.ImporterId, existingSource.ImporterId, StringComparison.Ordinal) ==
		                      false;
		var importSettingsChanged =
			string.Equals(importSettingsJson, existingSource.ImportSettingsJson, StringComparison.Ordinal) == false;
		var fileSizeChanged = sourceInfo.Length != existingSource.SourceFileSize;
		var lastWriteChanged = sourceInfo.LastWriteTimeUtc.Ticks != existingSource.SourceLastWriteTimeUtcTicks;

		if (importerChanged || importerVersionChanged || importSettingsChanged)
		{
			return false;
		}

		if (fileSizeChanged == false && lastWriteChanged == false)
		{
			return true;
		}

		var sourceContentHash = AssetHashing.ComputeFileHash(absoluteSourcePath);
		if (string.Equals(sourceContentHash, existingSource.SourceContentHash, StringComparison.Ordinal) == false)
		{
			return false;
		}

		_index.UpdateSource(
			projectRootPath,
			existingSource.SourceId,
			relativeMetaPath,
			metadata.ImporterId,
			metadata.ImporterVersion,
			sourceContentHash,
			sourceInfo.Length,
			sourceInfo.LastWriteTimeUtc.Ticks,
			importSettingsJson);
		return true;
	}

	private AssetSourceMetaFile LoadOrCreateMetadata(string absoluteMetaPath, string relativeSourcePath)
	{
		if (TryLoadUsableMetadata(absoluteMetaPath, relativeSourcePath, out var metadata))
		{
			return metadata;
		}

		return CreateDefaultMetadata(relativeSourcePath);
	}

	private bool TryLoadUsableMetadata(string absoluteMetaPath, string relativeSourcePath,
		out AssetSourceMetaFile metadata)
	{
		metadata = null!;
		if (File.Exists(absoluteMetaPath) == false)
		{
			return false;
		}

		try
		{
			var loadedMetadata = _metadataStore.Load(absoluteMetaPath);
			if (TryGetImporterForPath(relativeSourcePath, out var expectedImporter) == false)
			{
				return false;
			}

			if (loadedMetadata.SourceId == Guid.Empty
			    || string.IsNullOrWhiteSpace(loadedMetadata.ImporterId)
			    || string.Equals(loadedMetadata.ImporterId, expectedImporter.Id, StringComparison.Ordinal) == false
			    || loadedMetadata.ImporterVersion <= 0)
			{
				return false;
			}

			NormalizeImportSettingsJson(loadedMetadata);

			loadedMetadata.SubAssets ??= new List<AssetSubAssetManifestEntry>();
			metadata = loadedMetadata;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private AssetDatabase ImportAllSupportedSources(
		string projectRootPath,
		bool loadExistingSources,
		ISet<string>? reimportedSourcePaths = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
		InitializeProject(projectRootPath);
		var assetsPath = AssetPipelinePaths.GetAssetsPath(projectRootPath);
		var existingSources = loadExistingSources ? _index.GetSources(projectRootPath) : [];
		var indexedSourcesByPath =
			existingSources.ToDictionary(source => source.RelativeSourcePath, StringComparer.OrdinalIgnoreCase);
		var sourceFiles = EnumerateSupportedSourceFiles(assetsPath);
		LogLibraryBuildStage($"Enumerated {sourceFiles.Count} supported source files.");

		var knownRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		for (var i = 0; i < sourceFiles.Count; i++)
		{
			var absoluteSourcePath = sourceFiles[i];
			var relativeSourcePath = ToProjectRelativePath(projectRootPath, absoluteSourcePath);
			knownRelativePaths.Add(relativeSourcePath);

			if (loadExistingSources
			    && indexedSourcesByPath.TryGetValue(relativeSourcePath, out var existingSource)
			    && TryRefreshSourceScanState(projectRootPath, absoluteSourcePath, relativeSourcePath, existingSource))
			{
				continue;
			}

			if (TryImportSourceDuringRefresh(
				projectRootPath,
				absoluteSourcePath,
				relativeSourcePath,
				loadExistingSources && indexedSourcesByPath.TryGetValue(relativeSourcePath, out existingSource)
					? existingSource
					: null))
			{
				reimportedSourcePaths?.Add(relativeSourcePath);
			}
		}

		if (loadExistingSources)
		{
			for (var i = 0; i < existingSources.Count; i++)
			{
				if (knownRelativePaths.Contains(existingSources[i].RelativeSourcePath))
				{
					continue;
				}

				DeleteIndexedSource(projectRootPath, existingSources[i].SourceId);
			}
		}

		LogLibraryBuildStage("Loading completed asset database.");
		return LoadDatabase(projectRootPath);
	}

	private bool TryImportSourceDuringRefresh(
		string projectRootPath,
		string absoluteSourcePath,
		string relativeSourcePath,
		AssetSourceRecord? existingSource)
	{
		try
		{
			ImportSource(projectRootPath, absoluteSourcePath, relativeSourcePath, existingSource);
			return true;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine(
				$"Failed to import asset source '{relativeSourcePath}'. The source will be skipped during this refresh. {ex.GetType().Name}: {ex.Message}");
			return false;
		}
	}

	private List<string> EnumerateSupportedSourceFiles(string assetsPath)
	{
		if (Directory.Exists(assetsPath) == false)
		{
			return [];
		}

		return Directory.EnumerateFiles(assetsPath, "*", SearchOption.AllDirectories)
			.Where(path => path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) == false)
			.Where(path => TryGetImporterForPath(path, out _))
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private static void RecreateLibrary(string projectRootPath)
	{
		SqliteConnection.ClearAllPools();
		var libraryPath = AssetPipelinePaths.GetLibraryPath(projectRootPath);
		if (Directory.Exists(libraryPath) == false)
		{
			return;
		}

		var libraryDirectory = new DirectoryInfo(libraryPath);
		foreach (var directory in libraryDirectory.EnumerateDirectories())
		{
			directory.Delete(recursive: true);
		}

		foreach (var file in libraryDirectory.EnumerateFiles())
		{
			file.Delete();
		}
	}

	private AssetSourceMetaFile CreateDefaultMetadata(string relativeSourcePath)
	{
		if (TryGetImporterForPath(relativeSourcePath, out var importer) == false)
		{
			throw new InvalidOperationException($"Unsupported asset source '{relativeSourcePath}'.");
		}

		return new AssetSourceMetaFile
		{
			SourceId = Guid.NewGuid(),
			ImporterId = importer.Id,
			ImporterVersion = importer.Version,
			ImportSettingsJson = importer.CreateDefaultSettingsJson()
		};
	}

	private bool TryGetImporterForPath(string relativeSourcePath, out AssetImporterDescriptor importer)
	{
		for (var i = 0; i < _importers.Count; i++)
		{
			if (_importers[i].CanImport(relativeSourcePath))
			{
				importer = _importers[i];
				return true;
			}
		}

		importer = null!;
		return false;
	}

	private bool TryGetImporterById(string importerId, out AssetImporterDescriptor importer)
	{
		for (var i = 0; i < _importers.Count; i++)
		{
			if (string.Equals(_importers[i].Id, importerId, StringComparison.Ordinal))
			{
				importer = _importers[i];
				return true;
			}
		}

		importer = null!;
		return false;
	}

	private IReadOnlyList<AssetImporterDescriptor> CreateImporters()
	{
		return
		[
			new AssetImporterDescriptor(
				AssetImporterIds.Material,
				1,
				path => path.EndsWith(MaterialAsset.FileExtension, StringComparison.OrdinalIgnoreCase),
				() => "{}",
				ImportMaterialSource),
			new AssetImporterDescriptor(
				AssetImporterIds.DataAsset,
				1,
				path => path.EndsWith(DataAssetFile.FileExtension, StringComparison.OrdinalIgnoreCase),
				() => "{}",
				ImportDataAssetSource),
			new AssetImporterDescriptor(
				AssetImporterIds.Terrain,
				1,
				path => path.EndsWith(TerrainAsset.FileExtension, StringComparison.OrdinalIgnoreCase),
				() => "{}",
				(projectRootPath, absoluteSourcePath, relativeSourcePath, relativeMetaPath, metadata) =>
					ImportTerrainSource(absoluteSourcePath, relativeSourcePath, relativeMetaPath, metadata)),
			new AssetImporterDescriptor(
				AssetImporterIds.EditorScene,
				1,
				path => path.EndsWith(EditorSceneAssetFile.FileExtension, StringComparison.OrdinalIgnoreCase),
				() => "{}",
				(projectRootPath, absoluteSourcePath, relativeSourcePath, relativeMetaPath, metadata) =>
					ImportEditorSceneSource(absoluteSourcePath, relativeSourcePath, relativeMetaPath, metadata)),
			new AssetImporterDescriptor(
				AssetImporterIds.SceneCell,
				1,
				path => path.EndsWith(Cell.FileExtension, StringComparison.OrdinalIgnoreCase),
				() => "{}",
				ImportSceneCellSource),
			new AssetImporterDescriptor(
				AssetImporterIds.EditorPrefab,
				1,
				path => path.EndsWith(PrefabAssetFile.FileExtension, StringComparison.OrdinalIgnoreCase),
				() => "{}",
				(projectRootPath, absoluteSourcePath, relativeSourcePath, relativeMetaPath, metadata) =>
					ImportPrefabSource(absoluteSourcePath, relativeSourcePath, relativeMetaPath, metadata)),
			new AssetImporterDescriptor(
				AssetImporterIds.Texture,
				1,
				path =>
				{
					var extension = Path.GetExtension(path);
					return string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
					       string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
					       string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ||
					       string.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase) ||
					       string.Equals(extension, ".tga", StringComparison.OrdinalIgnoreCase) ||
					       string.Equals(extension, ".psd", StringComparison.OrdinalIgnoreCase) ||
					       string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase) ||
					       string.Equals(extension, ".hdr", StringComparison.OrdinalIgnoreCase);
				},
				() => AssetPipelineSerialization.Serialize(new TextureImportSettings()),
				ImportTextureSource),
			new AssetImporterDescriptor(
				AssetImporterIds.Audio,
				1,
				AudioAssetConstants.IsSupportedSource,
				() => AssetPipelineSerialization.Serialize(new AudioImportSettings()),
				ImportAudioSource),
			new AssetImporterDescriptor(
				AssetImporterIds.ThreeDScene,
				7,
				path =>
				{
					var extension = Path.GetExtension(path);
					return string.Equals(extension, ".gltf", StringComparison.OrdinalIgnoreCase) ||
					       string.Equals(extension, ".glb", StringComparison.OrdinalIgnoreCase) ||
					       string.Equals(extension, ".fbx", StringComparison.OrdinalIgnoreCase);
				},
				() => AssetPipelineSerialization.Serialize(new ModelImportSettings()),
				ImportThreeDSource)
		];
	}

	private Guid GetOrCreateNodeId(AssetSourceMetaFile metadata, string key, AssetType type, string name)
	{
		var existing =
			metadata.SubAssets.FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.Ordinal));
		if (existing is not null)
		{
			existing.Type = type;
			existing.Name = name;
			return existing.NodeId;
		}

		var created = new AssetSubAssetManifestEntry
		{
			Key = key,
			NodeId = Guid.NewGuid(),
			Type = type,
			Name = name
		};
		metadata.SubAssets.Add(created);
		return created.NodeId;
	}

	private static IReadOnlyList<MaterialDependency> CreateMaterialDependencies(MaterialAsset materialAsset)
	{
		var properties = materialAsset.GetActiveProperties();
		var dependencies = new List<MaterialDependency>(5);
		AddDependency(properties.Textures.Albedo, "material-texture:albedo");
		AddDependency(properties.Textures.Orm, "material-texture:orm");
		AddDependency(properties.Textures.Normal, "material-texture:normal");
		AddDependency(properties.Textures.Emissive, "material-texture:emissive");
		return dependencies;

		void AddDependency(AssetRef<Texture> reference, string kind)
		{
			if (reference.NodeId == Guid.Empty)
			{
				return;
			}

			dependencies.Add(new MaterialDependency(reference.NodeId, kind));
		}
	}

	private static MaterialAsset CreateGeneratedMaterialAsset(
		ImportedMaterial importedMaterial,
		IReadOnlyList<Guid> textureNodeIds,
		int? ormTextureIndex)
	{
		var materialType = importedMaterial.AlphaMode switch
		{
			AlphaMode.AlphaTest => MaterialAssetType.AlphaTest,
			AlphaMode.AlphaBlend => MaterialAssetType.AlphaBlend,
			_ => MaterialAssetType.Opaque
		};

		var materialAsset = new MaterialAsset
		{
			MaterialType = materialType
		};
		var properties = materialAsset.GetActiveProperties();
		properties.BaseColor = importedMaterial.BaseColor;
		properties.MetallicFactor = importedMaterial.MetallicFactor;
		properties.RoughnessFactor = importedMaterial.RoughnessFactor;
		properties.NormalScale = importedMaterial.NormalScale;
		properties.EmissiveFactor = importedMaterial.EmissiveFactor;
		properties.EmissiveIntensity = importedMaterial.EmissiveIntensity;
		properties.Textures.Albedo = CreateTextureRef(importedMaterial.BaseColorTextureIndex, textureNodeIds);
		properties.Textures.Normal = CreateTextureRef(importedMaterial.NormalTextureIndex, textureNodeIds);
		properties.Textures.Orm = CreateTextureRef(ormTextureIndex, textureNodeIds);
		properties.Textures.Emissive = CreateTextureRef(importedMaterial.EmissiveTextureIndex, textureNodeIds);

		materialAsset.AlphaCutoff = importedMaterial.AlphaCutoff;

		return materialAsset;
	}

	private static AssetRef<Texture> CreateTextureRef(int? index, IReadOnlyList<Guid> textureNodeIds)
	{
		if (index is not { } resolvedIndex || resolvedIndex < 0 || resolvedIndex >= textureNodeIds.Count)
		{
			return default;
		}

		return new AssetRef<Texture> { NodeId = textureNodeIds[resolvedIndex] };
	}

	private static ImportedTexture? ResolveImportedTexture(int? index, IReadOnlyList<ImportedTexture> textures)
	{
		if (index is not { } resolvedIndex || resolvedIndex < 0 || resolvedIndex >= textures.Count)
		{
			return null;
		}

		return textures[resolvedIndex];
	}

	private static ImportedTexture CreateOrmImportedTexture(
		ImportedTexture? metallicRoughnessTexture,
		ImportedTexture? occlusionTexture,
		int materialIndex)
	{
		var sourceTexture = metallicRoughnessTexture ?? occlusionTexture
			?? throw new InvalidOperationException("ORM texture generation requires at least one source texture.");
		var width = sourceTexture.Width;
		var height = sourceTexture.Height;
		var ormPixels = new byte[width * height * 4];
		var metallicRoughnessPixels = metallicRoughnessTexture?.PixelData;
		var occlusionPixels = occlusionTexture?.PixelData;

		for (var pixelOffset = 0; pixelOffset < ormPixels.Length; pixelOffset += 4)
		{
			ormPixels[pixelOffset + 0] = occlusionPixels is not null && pixelOffset < occlusionPixels.Length
				? occlusionPixels[pixelOffset + 0]
				: (byte)255;
			ormPixels[pixelOffset + 1] =
				metallicRoughnessPixels is not null && pixelOffset + 1 < metallicRoughnessPixels.Length
					? metallicRoughnessPixels[pixelOffset + 1]
					: (byte)255;
			ormPixels[pixelOffset + 2] =
				metallicRoughnessPixels is not null && pixelOffset + 2 < metallicRoughnessPixels.Length
					? metallicRoughnessPixels[pixelOffset + 2]
					: (byte)255;
			ormPixels[pixelOffset + 3] = 255;
		}

		var name = metallicRoughnessTexture?.NameOrPath
		           ?? occlusionTexture?.NameOrPath
		           ?? $"Material_{materialIndex}_ORM";
		return new ImportedTexture(
			$"{Path.GetFileNameWithoutExtension(name)}_ORM.png",
			width,
			height,
			false,
			TextureSemantic.MetallicRoughness,
			[new TextureMipData(width, height, ormPixels)]);
	}

	private List<AssetArtifactRecord> WriteTextureArtifacts(
		string projectRootPath,
		Guid nodeId,
		string relativeImportedPath,
		ImportedTexture importedTexture)
	{
		LogLibraryBuildStage($"Writing texture artifacts for '{importedTexture.NameOrPath}' ({importedTexture.Width}x{importedTexture.Height}, {importedTexture.Semantic}).");
		ImportedTextureSerializer.Write(GetAbsolutePath(projectRootPath, relativeImportedPath), importedTexture);

		var d3d12RelativePath = NormalizeRelativePath(Path.Combine(
			AssetPipelinePaths.LibraryFolderName,
			AssetPipelinePaths.ArtifactsFolderName,
			nodeId.ToString("D"),
			"runtime-d3d12.bin"));
		var metalRelativePath = NormalizeRelativePath(Path.Combine(
			AssetPipelinePaths.LibraryFolderName,
			AssetPipelinePaths.ArtifactsFolderName,
			nodeId.ToString("D"),
			"runtime-metal.bin"));
		var artifacts = new List<(string ArtifactKey, string Target, string RelativePath, string AbsolutePath)>(1);
		LogLibraryBuildStage($"Building runtime texture for '{importedTexture.NameOrPath}'.");
		var texture = CreateRuntimeTexture(importedTexture);
		LogLibraryBuildStage($"Built runtime texture for '{importedTexture.NameOrPath}' ({texture.MipCount} mips, {texture.Format}).");
		var compressionFamily = TextureCompressionCompiler.TryGetBcRuntimeFormat(importedTexture.Semantic, out _)
			? TextureCompressionFamily.Bc
			: TextureCompressionFamily.None;
		if (OperatingSystem.IsMacOS())
		{
			var metalAbsolutePath = GetAbsolutePath(projectRootPath, metalRelativePath);
			TextureArtifactSerializer.Write(
				metalAbsolutePath,
				texture,
				importedTexture.Semantic,
				compressionFamily);
			artifacts.Add(("runtime-metal", "metal", metalRelativePath, metalAbsolutePath));
		}
		else
		{
			var d3d12AbsolutePath = GetAbsolutePath(projectRootPath, d3d12RelativePath);
			TextureArtifactSerializer.Write(
				d3d12AbsolutePath,
				texture,
				importedTexture.Semantic,
				compressionFamily);
			artifacts.Add(("runtime-d3d12", "d3d12", d3d12RelativePath, d3d12AbsolutePath));
		}

		return CreateTextureArtifactRecords(nodeId, artifacts);
	}

	private Texture CreateRuntimeTexture(ImportedTexture importedTexture)
	{
		if (TextureCompressionCompiler.TryGetBcRuntimeFormat(importedTexture.Semantic, out _) == false)
		{
			return new Texture(
				importedTexture.NameOrPath,
				importedTexture.Width,
				importedTexture.Height,
				importedTexture.IsSrgb,
				TextureFormat.Rgba8Unorm,
				TextureMipGenerator.GenerateRgba32MipChain(importedTexture.MipLevels[0]));
		}

		return _textureGpuCompressionService.CompileBcTexture(importedTexture);
	}

	private void BeginLibraryBuildDiagnostics(string message)
	{
		_libraryBuildStopwatch = Stopwatch.StartNew();
		LogLibraryBuildStage(message);
	}

	private void LogLibraryBuildStage(string message)
	{
		var stopwatch = _libraryBuildStopwatch;
		if (stopwatch is null)
		{
			return;
		}

		using var process = Process.GetCurrentProcess();
		var managedMiB = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);
		var workingSetMiB = process.WorkingSet64 / (1024.0 * 1024.0);
		var privateBytesMiB = process.PrivateMemorySize64 / (1024.0 * 1024.0);
		Console.Error.WriteLine(
			$"[Library Build +{stopwatch.Elapsed.TotalSeconds:F1}s | managed {managedMiB:F0} MiB | working set {workingSetMiB:F0} MiB | private {privateBytesMiB:F0} MiB] {message}");
	}

	private sealed class UnsupportedTextureGpuCompressionService : ITextureGpuCompressionService
	{
		public Texture CompileBcTexture(ImportedTexture importedTexture)
		{
			throw new InvalidOperationException(
				$"GPU BC compression service is not available while importing '{importedTexture.NameOrPath}'.");
		}
	}

	private static List<AssetArtifactRecord> CreateTextureArtifactRecords(
		Guid nodeId,
		IReadOnlyList<(string ArtifactKey, string Target, string RelativePath, string AbsolutePath)> artifacts)
	{
		var results = new List<AssetArtifactRecord>(artifacts.Count);
		for (var i = 0; i < artifacts.Count; i++)
		{
			var artifact = artifacts[i];
			var fileInfo = new FileInfo(artifact.AbsolutePath);
			var contentHash = AssetHashing.ComputeFileHash(artifact.AbsolutePath);
			results.Add(new AssetArtifactRecord
			{
				NodeId = nodeId,
				ArtifactKey = artifact.ArtifactKey,
				Kind = "RuntimeTexture",
				Target = artifact.Target,
				RelativePath = artifact.RelativePath,
				ContentHash = contentHash,
				ByteSize = fileInfo.Length,
				ChunkIndex = 0,
				ChunkCount = 1,
				StreamGroup = "default",
				MetadataJson = "{}"
			});
		}

		return results;
	}

	private AssetDatabaseEntry CreateDatabaseEntry(string projectRootPath, AssetNodeRecord node)
	{
		var entry = new AssetDatabaseEntry
		{
			Id = node.NodeId,
			SourceId = node.SourceId,
			Type = node.Type,
			Name = node.Name,
			NodeKey = node.NodeKey,
			IsGenerated = node.IsGenerated,
			RelativeSourcePath = node.RelativeSourcePath,
			RelativeAssetPath = node.RelativeAssetPath,
			RelativeStatePath = node.RelativeMetaPath,
			RelativeMetaPath = node.RelativeMetaPath,
			Artifacts = _index.GetArtifactsForNode(projectRootPath, node.NodeId).ToList(),
			SummaryJson = string.IsNullOrWhiteSpace(node.SummaryJson) ? "{}" : node.SummaryJson
		};

		return entry;
	}

	private void DeleteSourceArtifacts(string projectRootPath, Guid sourceId)
	{
		var nodes = _index.GetNodes(projectRootPath).Where(node => node.SourceId == sourceId).ToList();
		var importedDirectory =
			Path.Combine(AssetPipelinePaths.GetImportedRoot(projectRootPath), sourceId.ToString("D"));
		if (Directory.Exists(importedDirectory))
		{
			Directory.Delete(importedDirectory, recursive: true);
		}

		for (var i = 0; i < nodes.Count; i++)
		{
			var artifactDirectory = Path.Combine(AssetPipelinePaths.GetArtifactsRoot(projectRootPath),
				nodes[i].NodeId.ToString("D"));
			if (Directory.Exists(artifactDirectory))
			{
				Directory.Delete(artifactDirectory, recursive: true);
			}
		}
	}

	private void DeleteIndexedSource(string projectRootPath, Guid sourceId)
	{
		DeleteSourceArtifacts(projectRootPath, sourceId);
		_index.DeleteSource(projectRootPath, sourceId);
	}

	private static string ToProjectRelativePath(string projectRootPath, string absolutePath)
	{
		var relative = Path.GetRelativePath(projectRootPath, absolutePath);
		return NormalizeRelativePath(relative);
	}

	private static string NormalizeRelativePath(string relativePath)
	{
		return relativePath.Replace(Path.DirectorySeparatorChar, '/');
	}

	private static string GetAbsolutePath(string projectRootPath, string relativePath)
	{
		var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
		return Path.GetFullPath(Path.Combine(projectRootPath, normalized));
	}

	private static Dictionary<Guid, List<SavedEntity>> BuildPrefabChildrenMap(List<SavedEntity> entities)
	{
		var childrenByParent = new Dictionary<Guid, List<SavedEntity>>();
		for (var i = 0; i < entities.Count; i++)
		{
			if (entities[i].ParentEntityId is not { } parentEntityId)
			{
				continue;
			}

			if (childrenByParent.TryGetValue(parentEntityId, out var children) == false)
			{
				children = [];
				childrenByParent[parentEntityId] = children;
			}

			children.Add(entities[i]);
		}

		return childrenByParent;
	}

	private Entity InstantiatePrefabEntities(
		EditorScene scene,
		string projectRootPath,
		Guid prefabNodeId,
		SavedEntity rootEntity,
		Dictionary<Guid, SavedEntity> entitiesById,
		Dictionary<Guid, List<SavedEntity>> childrenByParent)
	{
		var resolvedEntitiesById =
			ResolvePrefabEntitiesForInstantiation(projectRootPath, rootEntity.EntityId, entitiesById, childrenByParent);
		var instantiatedEntitiesBySourceId = new Dictionary<Guid, Entity>(resolvedEntitiesById.Count);
		CreateInstantiatedPrefabEntities(scene, prefabNodeId, rootEntity.EntityId, resolvedEntitiesById,
			childrenByParent, instantiatedEntitiesBySourceId, parent: null);
		ApplyInstantiatedPrefabEntityState(scene, resolvedEntitiesById, instantiatedEntitiesBySourceId);
		return instantiatedEntitiesBySourceId[rootEntity.EntityId];
	}

	private static void ApplySpawnPosition(World world, Entity rootEntity, Vector3? spawnPosition)
	{
		if (spawnPosition is not { } position)
		{
			return;
		}

		if (world.HasComponent<LocalTransform>(rootEntity))
		{
			world.SetLocalPosition(rootEntity, position);
			return;
		}

		world.AddTransform(rootEntity, Matrix4x4.CreateTranslation(position));
	}

	private Dictionary<Guid, SavedEntity> ResolvePrefabEntitiesForInstantiation(
		string projectRootPath,
		Guid rootEntityId,
		IReadOnlyDictionary<Guid, SavedEntity> entitiesById,
		IReadOnlyDictionary<Guid, List<SavedEntity>> childrenByParent)
	{
		var resolvedEntitiesById = new Dictionary<Guid, SavedEntity>();
		var pendingEntityIds = new Stack<Guid>();
		pendingEntityIds.Push(rootEntityId);
		while (pendingEntityIds.Count > 0)
		{
			var entityId = pendingEntityIds.Pop();
			if (entitiesById.TryGetValue(entityId, out var sourceEntity) == false ||
			    resolvedEntitiesById.ContainsKey(entityId))
			{
				continue;
			}

			resolvedEntitiesById[entityId] = ResolvePrefabEntityForInstantiation(projectRootPath, sourceEntity);
			if (childrenByParent.TryGetValue(entityId, out var children) == false)
			{
				continue;
			}

			for (var i = 0; i < children.Count; i++)
			{
				pendingEntityIds.Push(children[i].EntityId);
			}
		}

		return resolvedEntitiesById;
	}

	private void CreateInstantiatedPrefabEntities(
		EditorScene scene,
		Guid prefabNodeId,
		Guid sourceEntityId,
		IReadOnlyDictionary<Guid, SavedEntity> resolvedEntitiesById,
		IReadOnlyDictionary<Guid, List<SavedEntity>> childrenByParent,
		Dictionary<Guid, Entity> instantiatedEntitiesBySourceId,
		Entity? parent)
	{
		if (resolvedEntitiesById.TryGetValue(sourceEntityId, out var sourceEntity) == false)
		{
			return;
		}

		var world = scene.World;
		var entity = CreateEntity(world, sourceEntity);
		instantiatedEntitiesBySourceId[sourceEntityId] = entity;
		scene.EntityIds[entity] = Guid.NewGuid();
		scene.EntityCellKeys[entity] = SceneCellKey.Global;
		scene.EntityPrefabSourcePaths[entity] = CreateInstantiatedPrefabSourcePath(prefabNodeId, sourceEntity);
		if (string.IsNullOrWhiteSpace(sourceEntity.Icon) == false)
		{
			scene.EntityIcons[entity] = sourceEntity.Icon;
		}

		if (parent is { } parentEntity)
		{
			world.SetParent(entity, parentEntity);
		}

		if (childrenByParent.TryGetValue(sourceEntityId, out var children) == false)
		{
			return;
		}

		for (var i = 0; i < children.Count; i++)
		{
			CreateInstantiatedPrefabEntities(scene, prefabNodeId, children[i].EntityId, resolvedEntitiesById,
				childrenByParent, instantiatedEntitiesBySourceId, entity);
		}
	}

	private static void ApplyInstantiatedPrefabEntityState(
		EditorScene scene,
		IReadOnlyDictionary<Guid, SavedEntity> resolvedEntitiesById,
		IReadOnlyDictionary<Guid, Entity> instantiatedEntitiesBySourceId)
	{
		foreach (var entry in instantiatedEntitiesBySourceId)
		{
			var sourceEntity = resolvedEntitiesById[entry.Key];
			var entity = entry.Value;
			scene.World.SetEnabled(entity, sourceEntity.Enabled);
			for (var i = 0; i < sourceEntity.Components.Count; i++)
			{
				ApplySavedComponent(scene, instantiatedEntitiesBySourceId, entity, sourceEntity.Components[i]);
			}
		}
	}

	private SavedEntity ResolvePrefabEntityForInstantiation(string projectRootPath, SavedEntity sourceEntity)
	{
		var resolvedEntity = EditorPrefabUtility.CloneEntity(sourceEntity);
		if (resolvedEntity.PrefabSourcePath.Count == 0)
		{
			return resolvedEntity;
		}

		if (TryResolveNestedPrefabSourceEntity(projectRootPath, resolvedEntity.PrefabSourcePath[0], new HashSet<Guid>(),
			    out var nestedSourceEntity))
		{
			resolvedEntity = EditorPrefabUtility.MergePrefabSourceEntity(resolvedEntity, nestedSourceEntity);
		}

		return resolvedEntity;
	}

	private bool TryResolveNestedPrefabSourceEntity(
		string projectRootPath,
		SavedPrefabLink sourceLink,
		HashSet<Guid> prefabAssetStack,
		out SavedEntity sourceEntity)
	{
		sourceEntity = null!;
		if (sourceLink.PrefabAssetId == Guid.Empty || sourceLink.PrefabEntityId == Guid.Empty)
		{
			return false;
		}

		if (prefabAssetStack.Add(sourceLink.PrefabAssetId) == false)
		{
			throw new InvalidOperationException(
				$"Cyclic prefab nesting detected while resolving prefab '{sourceLink.PrefabAssetId}'.");
		}

		if (_index.TryGetNode(projectRootPath, sourceLink.PrefabAssetId, out var prefabNode) == false ||
		    prefabNode.Type != AssetType.Prefab)
		{
			prefabAssetStack.Remove(sourceLink.PrefabAssetId);
			return false;
		}

		var prefabFile = PrefabAssetFile.Load(GetAbsolutePath(projectRootPath, prefabNode.RelativeAssetPath));
		var nestedSource = prefabFile.Entities.FirstOrDefault(entity => entity.EntityId == sourceLink.PrefabEntityId);
		if (nestedSource is null)
		{
			prefabAssetStack.Remove(sourceLink.PrefabAssetId);
			return false;
		}

		sourceEntity = EditorPrefabUtility.CloneEntity(nestedSource);
		if (sourceEntity.PrefabSourcePath.Count > 0 &&
		    TryResolveNestedPrefabSourceEntity(projectRootPath, sourceEntity.PrefabSourcePath[0], prefabAssetStack,
			    out var deepSourceEntity))
		{
			sourceEntity = EditorPrefabUtility.MergePrefabSourceEntity(sourceEntity, deepSourceEntity);
		}

		prefabAssetStack.Remove(sourceLink.PrefabAssetId);
		return true;
	}

	private static List<SavedPrefabLink> CreateInstantiatedPrefabSourcePath(Guid prefabNodeId, SavedEntity sourceEntity)
	{
		var sourcePath = new List<SavedPrefabLink>(1 + sourceEntity.PrefabSourcePath.Count)
		{
			new()
			{
				PrefabAssetId = prefabNodeId,
				PrefabEntityId = sourceEntity.EntityId
			}
		};
		sourcePath.AddRange(EditorPrefabUtility.ClonePrefabSourcePath(sourceEntity.PrefabSourcePath));
		return sourcePath;
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

	private static void ApplySavedComponent(EditorScene scene, IReadOnlyDictionary<Guid, Entity>? sourceEntitiesById,
		Entity entity, SavedComponent component)
	{
		if ((ProjectTypeResolverUtility.TryResolveFromLoadedAssemblies(component.TypeId, out var componentType) ==
		     false &&
		     ProjectTypeResolverUtility.TryResolveFromLoadedAssemblies(component.Type, out componentType) == false) ||
		    componentType == typeof(NameComponent) ||
		    componentType.IsValueType == false ||
		    typeof(IEntityComponent).IsAssignableFrom(componentType) == false)
		{
			return;
		}

		var deserialized = sourceEntitiesById is null
			? EditorEntityReferenceUtility.DeserializeComponentData(scene, component.Data, componentType)
			: EditorEntityReferenceUtility.DeserializeValue(component.Data, componentType, entityId =>
			{
				return sourceEntitiesById.TryGetValue(entityId, out var resolvedEntity)
					? resolvedEntity
					: null;
			});
		RuntimeComponentAccessor.WriteBoxed(scene.World, entity, componentType,
			deserialized ?? ProjectTypeStateTransferUtility.CreateDefaultValue(componentType));
	}

	private static string GetUniqueDestinationPath(string destinationFolder, string baseName, string extension)
	{
		var index = 0;
		while (true)
		{
			var fileName = index == 0 ? $"{baseName}{extension}" : $"{baseName} {index}{extension}";
			var candidate = Path.Combine(destinationFolder, fileName);
			if (File.Exists(candidate) == false)
			{
				return candidate;
			}

			index++;
		}
	}

	private static string SanitizeKey(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "unnamed";
		}

		var chars = value
			.Select(character => char.IsLetterOrDigit(character) ? character : '-')
			.ToArray();
		return new string(chars);
	}

	private static void WriteJsonFile<T>(string absolutePath, T value)
	{
		var directory = Path.GetDirectoryName(absolutePath);
		if (string.IsNullOrWhiteSpace(directory) == false)
		{
			Directory.CreateDirectory(directory);
		}

		File.WriteAllText(absolutePath, JsonSerializer.Serialize(value, AssetJson.SerializerOptions));
	}

	private sealed class ImportGraph
	{
		public required List<AssetNodeRecord> Nodes { get; init; }
		public required List<AssetArtifactRecord> Artifacts { get; init; }
		public required List<AssetDependencyRecord> Dependencies { get; init; }
	}

	private readonly record struct MaterialDependency(Guid TargetNodeId, string Kind);

	private delegate ImportGraph ImportSourceDelegate(
		string projectRootPath,
		string absoluteSourcePath,
		string relativeSourcePath,
		string relativeMetaPath,
		AssetSourceMetaFile metadata);

	private sealed class AssetImporterDescriptor
	{
		private readonly Func<string, bool> _canImport;
		private readonly Func<string> _createDefaultSettingsJson;
		private readonly ImportSourceDelegate _import;

		public AssetImporterDescriptor(
			string id,
			int version,
			Func<string, bool> canImport,
			Func<string> createDefaultSettingsJson,
			ImportSourceDelegate import)
		{
			Id = id;
			Version = version;
			_canImport = canImport ?? throw new ArgumentNullException(nameof(canImport));
			_createDefaultSettingsJson = createDefaultSettingsJson ??
			                             throw new ArgumentNullException(nameof(createDefaultSettingsJson));
			_import = import ?? throw new ArgumentNullException(nameof(import));
		}

		public string Id { get; }
		public int Version { get; }

		public bool CanImport(string relativePath) => _canImport(relativePath);
		public string CreateDefaultSettingsJson() => _createDefaultSettingsJson();

		public ImportGraph Import(
			string projectRootPath,
			string absoluteSourcePath,
			string relativeSourcePath,
			string relativeMetaPath,
			AssetSourceMetaFile metadata)
		{
			return _import(projectRootPath, absoluteSourcePath, relativeSourcePath, relativeMetaPath, metadata);
		}
	}
}
