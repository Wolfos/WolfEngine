using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.UI;

namespace WolfEngine.Editor.Projects;

public interface IEditorProjectService
{
	bool HasOpenProject { get; }
	string? ProjectRootPath { get; }
	string? AssetsPath { get; }
	string? LibraryPath { get; }
	string? DatabasePath { get; }
	string? GameplayProjectRelativePath { get; }
	string? GameplayProjectPath { get; }
	AssetDatabase CurrentAssetDatabase { get; }

	bool CreateProject(string parentFolder, string projectName, out string errorMessage);
	bool OpenProject(string projectRoot, out string errorMessage);
	void CloseProject();
	AssetDatabaseRefreshResult ReloadAssetDatabase();
	void ReloadAssetDatabaseFromIndex();
	void RefreshAssetSource(string relativeSourcePath);
	void SaveAssetDatabase(AssetDatabase database);
	AssetDatabase CloneCurrentAssetDatabase();
	bool TryGetAsset(Guid assetId, out AssetDatabaseEntry asset);
	string GetAbsolutePath(string relativePath);
	void DeleteAssetSource(string relativeSourcePath);
	void DeleteFolder(string relativeFolderPath);
}

public readonly record struct AssetDatabaseRefreshResult(IReadOnlyCollection<Guid> InvalidatedAssetIds)
{
	public static AssetDatabaseRefreshResult Empty { get; } = new(Array.Empty<Guid>());
}

public sealed class EditorProjectService : IEditorProjectService
{
	private readonly IProjectAssetPipelineService _assetPipelineService;
	private readonly IAssetInstanceRegistry _assetInstanceRegistry;
	private readonly IEditorNotificationService? _notificationService;
	private readonly IServiceProvider? _serviceProvider;
	private AssetDatabase _currentAssetDatabase = new();
	private string? _projectRootPath;
	private EditorProjectManifest? _projectManifest;

	public EditorProjectService(
		IProjectAssetPipelineService assetPipelineService,
		IAssetInstanceRegistry assetInstanceRegistry,
		IEditorNotificationService? notificationService = null,
		IServiceProvider? serviceProvider = null)
	{
		_assetPipelineService = assetPipelineService ?? throw new ArgumentNullException(nameof(assetPipelineService));
		_assetInstanceRegistry = assetInstanceRegistry ?? throw new ArgumentNullException(nameof(assetInstanceRegistry));
		_notificationService = notificationService;
		_serviceProvider = serviceProvider;
		_assetInstanceRegistry.Clear();
	}

	public bool HasOpenProject => string.IsNullOrWhiteSpace(_projectRootPath) == false;
	public string? ProjectRootPath => _projectRootPath;
	public string? AssetsPath => HasOpenProject ? AssetPipelinePaths.GetAssetsPath(_projectRootPath!) : null;
	public string? LibraryPath => HasOpenProject ? AssetPipelinePaths.GetLibraryPath(_projectRootPath!) : null;
	public string? DatabasePath => LibraryPath;
	public string? GameplayProjectRelativePath => HasOpenProject ? _projectManifest?.GameplayProjectRelativePath : null;
	public string? GameplayProjectPath => HasOpenProject && string.IsNullOrWhiteSpace(_projectManifest?.GameplayProjectRelativePath) == false
		? GetAbsolutePath(_projectManifest.GameplayProjectRelativePath)
		: null;
	public AssetDatabase CurrentAssetDatabase => _currentAssetDatabase;

	public bool CreateProject(string parentFolder, string projectName, out string errorMessage)
	{
		errorMessage = string.Empty;
		if (string.IsNullOrWhiteSpace(parentFolder))
		{
			errorMessage = "Choose a parent folder for the new project.";
			return false;
		}

		if (Directory.Exists(parentFolder) == false)
		{
			errorMessage = $"Parent folder '{parentFolder}' does not exist.";
			return false;
		}

		projectName = projectName.Trim();
		if (string.IsNullOrWhiteSpace(projectName))
		{
			errorMessage = "Enter a project name.";
			return false;
		}

		if (projectName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
		{
			errorMessage = "Project name contains invalid characters.";
			return false;
		}

		var projectRoot = Path.GetFullPath(Path.Combine(parentFolder, projectName));
		if (Directory.Exists(projectRoot) || File.Exists(projectRoot))
		{
			errorMessage = $"Project folder '{projectRoot}' already exists.";
			return false;
		}

		try
		{
			Directory.CreateDirectory(projectRoot);
			_assetPipelineService.InitializeProject(projectRoot);
			ProjectGameplayScaffolder.Scaffold(projectRoot, projectName);
			EditorProjectManifestFile.Save(projectRoot, new EditorProjectManifest
			{
				GameplayProjectRelativePath = ProjectGameplayScaffolder.GetGameplayProjectRelativePath(projectName)
			});
		}
		catch (Exception ex)
		{
			errorMessage = $"Failed to create project: {ex.Message}";
			return false;
		}

		return OpenProject(projectRoot, out errorMessage);
	}

	public bool OpenProject(string projectRoot, out string errorMessage)
	{
		errorMessage = string.Empty;
		if (string.IsNullOrWhiteSpace(projectRoot))
		{
			errorMessage = "Choose a project folder to open.";
			return false;
		}

		var fullProjectRoot = Path.GetFullPath(projectRoot);
		var assetsPath = AssetPipelinePaths.GetAssetsPath(fullProjectRoot);
		var libraryPath = AssetPipelinePaths.GetLibraryPath(fullProjectRoot);

		if (Directory.Exists(fullProjectRoot) == false)
		{
			errorMessage = $"Project folder '{fullProjectRoot}' does not exist.";
			return false;
		}

		if (Directory.Exists(assetsPath) == false)
		{
			errorMessage = "Project folder must contain an Assets subfolder.";
			return false;
		}

		if (File.Exists(EditorProjectManifestFile.GetPath(fullProjectRoot)) == false)
		{
			errorMessage = $"Project folder must contain a {EditorProjectManifestFile.FileName} manifest.";
			return false;
		}

		try
		{
			var manifest = EditorProjectManifestFile.Load(fullProjectRoot);
			ValidateManifest(fullProjectRoot, manifest);
			var shouldRebuildAssetDatabase = Directory.Exists(libraryPath) == false;
			_projectRootPath = fullProjectRoot;
			_projectManifest = manifest;
			_assetInstanceRegistry.Clear();
			ApplyDatabase(shouldRebuildAssetDatabase
				? _assetPipelineService.RebuildProject(_projectRootPath)
				: _assetPipelineService.RefreshProjectIncremental(_projectRootPath));
			if (shouldRebuildAssetDatabase)
			{
				_notificationService?.ReportInfo("Library folder was missing. Rebuilt the asset database from project sources.");
			}
			ClearUndoHistory();
			return true;
		}
		catch (Exception ex)
		{
			ReleaseAssetDatabaseConnections();
			_projectRootPath = null;
			_projectManifest = null;
			_currentAssetDatabase = new AssetDatabase();
			_assetInstanceRegistry.Clear();
			errorMessage = $"Failed to open project: {ex.Message}";
			Console.WriteLine(ex.Message);
			Console.WriteLine(ex.StackTrace);
			return false;
		}
	}

	public void CloseProject()
	{
		ReleaseAssetDatabaseConnections();
		_projectRootPath = null;
		_projectManifest = null;
		_currentAssetDatabase = new AssetDatabase();
		_assetInstanceRegistry.Clear();
		ClearUndoHistory();
	}

	public AssetDatabaseRefreshResult ReloadAssetDatabase()
	{
		ClearUndoHistory();
		if (HasOpenProject == false)
		{
			_currentAssetDatabase = new AssetDatabase();
			_assetInstanceRegistry.Clear();
			return AssetDatabaseRefreshResult.Empty;
		}

		var previousDatabase = CloneCurrentAssetDatabase();
		var refreshedDatabase = _assetPipelineService.RefreshProjectIncremental(_projectRootPath!);
		ApplyDatabase(refreshedDatabase);

		var changedNodeIds = CollectChangedNodeIds(previousDatabase, refreshedDatabase);
		var invalidatedNodeIds = _assetPipelineService.ExpandInvalidationClosure(_projectRootPath!, changedNodeIds);
		_assetInstanceRegistry.InvalidateAssets(invalidatedNodeIds);
		return new AssetDatabaseRefreshResult(invalidatedNodeIds);
	}

	public void ReloadAssetDatabaseFromIndex()
	{
		ClearUndoHistory();
		if (HasOpenProject == false)
		{
			_currentAssetDatabase = new AssetDatabase();
			_assetInstanceRegistry.Clear();
			return;
		}

		ApplyDatabase(_assetPipelineService.LoadDatabase(_projectRootPath!));
	}

	public void RefreshAssetSource(string relativeSourcePath)
	{
		if (HasOpenProject == false)
		{
			throw new InvalidOperationException("No project is currently open.");
		}

		ArgumentException.ThrowIfNullOrWhiteSpace(relativeSourcePath);
		var normalizedRelativePath = relativeSourcePath.Replace(Path.DirectorySeparatorChar, '/');
		var previousNodeIds = _currentAssetDatabase.Assets
			.Where(asset => string.Equals(asset.RelativeSourcePath, normalizedRelativePath, StringComparison.OrdinalIgnoreCase))
			.Select(asset => asset.Id)
			.ToHashSet();

		_assetPipelineService.ReimportSource(_projectRootPath!, normalizedRelativePath);
		var database = _assetPipelineService.LoadDatabase(_projectRootPath!);
		var currentNodeIds = database.Assets
			.Where(asset => string.Equals(asset.RelativeSourcePath, normalizedRelativePath, StringComparison.OrdinalIgnoreCase))
			.Select(asset => asset.Id)
			.ToHashSet();
		previousNodeIds.UnionWith(currentNodeIds);

		ApplyDatabase(database);
		var invalidatedNodeIds = _assetPipelineService.ExpandInvalidationClosure(_projectRootPath!, previousNodeIds);
		_assetInstanceRegistry.InvalidateAssets(invalidatedNodeIds);
	}

	public void SaveAssetDatabase(AssetDatabase database)
	{
		ArgumentNullException.ThrowIfNull(database);
		ReloadAssetDatabaseFromIndex();
	}

	public AssetDatabase CloneCurrentAssetDatabase()
	{
		return new AssetDatabase
		{
			Assets = _currentAssetDatabase.Assets.Select(CloneEntry).ToList()
		};
	}

	public bool TryGetAsset(Guid assetId, out AssetDatabaseEntry asset)
	{
		for (var i = 0; i < _currentAssetDatabase.Assets.Count; i++)
		{
			var candidate = _currentAssetDatabase.Assets[i];
			if (candidate.Id == assetId)
			{
				asset = CloneEntry(candidate);
				return true;
			}
		}

		asset = null!;
		return false;
	}

	public string GetAbsolutePath(string relativePath)
	{
		if (HasOpenProject == false)
		{
			throw new InvalidOperationException("No project is currently open.");
		}

		if (string.IsNullOrWhiteSpace(relativePath))
		{
			throw new ArgumentException("Relative path cannot be null or empty.", nameof(relativePath));
		}

		var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
		return Path.GetFullPath(Path.Combine(_projectRootPath!, normalized));
	}

	public void DeleteAssetSource(string relativeSourcePath)
	{
		if (HasOpenProject == false)
		{
			throw new InvalidOperationException("No project is currently open.");
		}

		var normalizedRelativePath = ProjectPathUtility.NormalizeRelativePath(relativeSourcePath);
		if (ProjectPathUtility.IsAssetsPathOrDescendant(normalizedRelativePath) == false)
		{
			throw new InvalidOperationException($"Path '{relativeSourcePath}' must be inside the Assets folder.");
		}

		var absoluteSourcePath = GetAbsolutePath(normalizedRelativePath);
		var absoluteMetaPath = absoluteSourcePath + ".meta";
		if (File.Exists(absoluteSourcePath) == false && File.Exists(absoluteMetaPath) == false)
		{
			throw new FileNotFoundException($"Asset source '{normalizedRelativePath}' was not found.");
		}

		if (File.Exists(absoluteSourcePath))
		{
			File.Delete(absoluteSourcePath);
		}

		if (File.Exists(absoluteMetaPath))
		{
			File.Delete(absoluteMetaPath);
		}

		_assetPipelineService.RemoveDeletedSource(_projectRootPath!, normalizedRelativePath);
		ReloadAssetDatabaseFromIndex();
	}

	public void DeleteFolder(string relativeFolderPath)
	{
		if (HasOpenProject == false)
		{
			throw new InvalidOperationException("No project is currently open.");
		}

		var normalizedRelativePath = ProjectPathUtility.NormalizeAssetsFolderPath(relativeFolderPath);
		if (string.Equals(normalizedRelativePath, AssetPipelinePaths.AssetsFolderName, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("Cannot delete the root Assets folder.");
		}

		var absoluteFolderPath = GetAbsolutePath(normalizedRelativePath);
		if (Directory.Exists(absoluteFolderPath) == false)
		{
			throw new DirectoryNotFoundException($"Folder '{normalizedRelativePath}' was not found.");
		}

		Directory.Delete(absoluteFolderPath, recursive: true);
		_assetPipelineService.RemoveDeletedSourcesUnderFolder(_projectRootPath!, normalizedRelativePath);
		ReloadAssetDatabaseFromIndex();
	}

	private void ApplyDatabase(AssetDatabase database)
	{
		ArgumentNullException.ThrowIfNull(database);
		_currentAssetDatabase = database;
		_assetInstanceRegistry.RefreshProject(_projectRootPath!, CloneCurrentAssetDatabase());
	}

	private static IReadOnlyCollection<Guid> CollectChangedNodeIds(AssetDatabase previousDatabase, AssetDatabase refreshedDatabase)
	{
		ArgumentNullException.ThrowIfNull(previousDatabase);
		ArgumentNullException.ThrowIfNull(refreshedDatabase);

		var previousAssets = previousDatabase.Assets.ToDictionary(asset => asset.Id);
		var refreshedAssets = refreshedDatabase.Assets.ToDictionary(asset => asset.Id);
		var changedNodeIds = new HashSet<Guid>();

		foreach (var entry in previousAssets)
		{
			if (refreshedAssets.TryGetValue(entry.Key, out var refreshedAsset) == false ||
			    AssetEntriesEqual(entry.Value, refreshedAsset) == false)
			{
				changedNodeIds.Add(entry.Key);
			}
		}

		foreach (var entry in refreshedAssets)
		{
			if (previousAssets.ContainsKey(entry.Key) == false)
			{
				changedNodeIds.Add(entry.Key);
			}
		}

		return changedNodeIds.ToArray();
	}

	private static bool AssetEntriesEqual(AssetDatabaseEntry left, AssetDatabaseEntry right)
	{
		return left.Id == right.Id &&
		       left.SourceId == right.SourceId &&
		       left.Type == right.Type &&
		       string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
		       string.Equals(left.NodeKey, right.NodeKey, StringComparison.Ordinal) &&
		       left.IsGenerated == right.IsGenerated &&
		       string.Equals(left.RelativeSourcePath, right.RelativeSourcePath, StringComparison.Ordinal) &&
		       string.Equals(left.RelativeAssetPath, right.RelativeAssetPath, StringComparison.Ordinal) &&
		       string.Equals(left.RelativeStatePath, right.RelativeStatePath, StringComparison.Ordinal) &&
		       string.Equals(left.RelativeMetaPath, right.RelativeMetaPath, StringComparison.Ordinal) &&
		       TextureSummariesEqual(left.TextureSummary, right.TextureSummary) &&
		       MaterialSummariesEqual(left.MaterialSummary, right.MaterialSummary) &&
		       DataAssetSummariesEqual(left.DataAssetSummary, right.DataAssetSummary) &&
		       MeshSummariesEqual(left.MeshSummary, right.MeshSummary) &&
		       ModelSummariesEqual(left.ModelSummary, right.ModelSummary) &&
		       SceneSummariesEqual(left.SceneSummary, right.SceneSummary) &&
		       PrefabSummariesEqual(left.PrefabSummary, right.PrefabSummary) &&
		       ArtifactListsEqual(left.Artifacts, right.Artifacts);
	}

	private static bool TextureSummariesEqual(TextureAssetSummary? left, TextureAssetSummary? right)
	{
		return left is null && right is null ||
		       left is not null &&
		       right is not null &&
		       string.Equals(left.RelativeSourceAssetPath, right.RelativeSourceAssetPath, StringComparison.Ordinal) &&
		       string.Equals(left.RelativeImportedPath, right.RelativeImportedPath, StringComparison.Ordinal) &&
		       string.Equals(left.RelativeRuntimeArtifactPath, right.RelativeRuntimeArtifactPath, StringComparison.Ordinal) &&
		       left.Width == right.Width &&
		       left.Height == right.Height &&
		       left.Channels == right.Channels &&
		       left.IsSrgb == right.IsSrgb &&
		       string.Equals(left.SourceExtension, right.SourceExtension, StringComparison.Ordinal);
	}

	private static bool MaterialSummariesEqual(MaterialAssetSummary? left, MaterialAssetSummary? right)
	{
		return left is null && right is null ||
		       left is not null &&
		       right is not null &&
		       left.MaterialType == right.MaterialType;
	}

	private static bool DataAssetSummariesEqual(DataAssetSummary? left, DataAssetSummary? right)
	{
		return left is null && right is null ||
		       left is not null &&
		       right is not null &&
		       string.Equals(left.DataAssetType, right.DataAssetType, StringComparison.Ordinal) &&
		       string.Equals(left.DataAssetTypeId, right.DataAssetTypeId, StringComparison.Ordinal) &&
		       string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal);
	}

	private static bool MeshSummariesEqual(MeshAssetSummary? left, MeshAssetSummary? right)
	{
		return left is null && right is null ||
		       left is not null &&
		       right is not null &&
		       string.Equals(left.RelativeImportedMeshPath, right.RelativeImportedMeshPath, StringComparison.Ordinal) &&
		       left.VertexCount == right.VertexCount &&
		       left.IndexCount == right.IndexCount;
	}

	private static bool ModelSummariesEqual(Model3DAssetSummary? left, Model3DAssetSummary? right)
	{
		return left is null && right is null ||
		       left is not null &&
		       right is not null &&
		       string.Equals(left.RelativeImportedModelPath, right.RelativeImportedModelPath, StringComparison.Ordinal) &&
		       left.RootNodeCount == right.RootNodeCount;
	}

	private static bool SceneSummariesEqual(SceneAssetSummary? left, SceneAssetSummary? right)
	{
		return left is null && right is null ||
		       left is not null &&
		       right is not null &&
		       string.Equals(left.GlobalCellPath, right.GlobalCellPath, StringComparison.Ordinal) &&
		       left.SpatialCellCount == right.SpatialCellCount;
	}

	private static bool PrefabSummariesEqual(PrefabAssetSummary? left, PrefabAssetSummary? right)
	{
		return left is null && right is null ||
		       left is not null &&
		       right is not null &&
		       left.RootEntityId == right.RootEntityId &&
		       left.EntityCount == right.EntityCount;
	}

	private static bool ArtifactListsEqual(IReadOnlyList<AssetArtifactRecord> left, IReadOnlyList<AssetArtifactRecord> right)
	{
		if (ReferenceEquals(left, right))
		{
			return true;
		}

		if (left.Count != right.Count)
		{
			return false;
		}

		for (var i = 0; i < left.Count; i++)
		{
			if (ArtifactEqual(left[i], right[i]) == false)
			{
				return false;
			}
		}

		return true;
	}

	private static bool ArtifactEqual(AssetArtifactRecord left, AssetArtifactRecord right)
	{
		return left.NodeId == right.NodeId &&
		       string.Equals(left.ArtifactKey, right.ArtifactKey, StringComparison.Ordinal) &&
		       string.Equals(left.Kind, right.Kind, StringComparison.Ordinal) &&
		       string.Equals(left.Target, right.Target, StringComparison.Ordinal) &&
		       string.Equals(left.RelativePath, right.RelativePath, StringComparison.Ordinal) &&
		       string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal) &&
		       left.ByteSize == right.ByteSize &&
		       left.ChunkIndex == right.ChunkIndex &&
		       left.ChunkCount == right.ChunkCount &&
		       string.Equals(left.StreamGroup, right.StreamGroup, StringComparison.Ordinal) &&
		       string.Equals(left.MetadataJson, right.MetadataJson, StringComparison.Ordinal);
	}

	private static void ReleaseAssetDatabaseConnections()
	{
		SqliteConnection.ClearAllPools();
	}

	private static void ValidateManifest(string projectRootPath, EditorProjectManifest manifest)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
		ArgumentNullException.ThrowIfNull(manifest);

		if (string.IsNullOrWhiteSpace(manifest.GameplayProjectRelativePath))
		{
			throw new InvalidOperationException("Project manifest is missing the gameplay project path.");
		}

		var normalizedRelativePath = ProjectPathUtility.NormalizeRelativePath(manifest.GameplayProjectRelativePath).Trim('/');
		if (Path.IsPathRooted(normalizedRelativePath))
		{
			throw new InvalidOperationException("Project manifest gameplay project path must be relative to the project root.");
		}

		var absoluteGameplayProjectPath = Path.GetFullPath(Path.Combine(projectRootPath, normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
		var fullProjectRoot = Path.GetFullPath(projectRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var fullProjectRootWithSeparator = fullProjectRoot + Path.DirectorySeparatorChar;
		if (absoluteGameplayProjectPath.StartsWith(fullProjectRootWithSeparator, StringComparison.OrdinalIgnoreCase) == false)
		{
			throw new InvalidOperationException("Project manifest gameplay project path must remain inside the project root.");
		}

		if (File.Exists(absoluteGameplayProjectPath) == false)
		{
			throw new FileNotFoundException(
				$"Gameplay project '{normalizedRelativePath}' referenced by the project manifest was not found.",
				absoluteGameplayProjectPath);
		}

		manifest.GameplayProjectRelativePath = normalizedRelativePath;
	}

	private static AssetDatabaseEntry CloneEntry(AssetDatabaseEntry asset)
	{
		return new AssetDatabaseEntry
		{
			Id = asset.Id,
			SourceId = asset.SourceId,
			Type = asset.Type,
			Name = asset.Name,
			NodeKey = asset.NodeKey,
			IsGenerated = asset.IsGenerated,
			RelativeSourcePath = asset.RelativeSourcePath,
			RelativeAssetPath = asset.RelativeAssetPath,
			RelativeStatePath = asset.RelativeStatePath,
			RelativeMetaPath = asset.RelativeMetaPath,
			Artifacts = asset.Artifacts.Select(artifact => new AssetArtifactRecord
			{
				NodeId = artifact.NodeId,
				ArtifactKey = artifact.ArtifactKey,
				Kind = artifact.Kind,
				Target = artifact.Target,
				RelativePath = artifact.RelativePath,
				ContentHash = artifact.ContentHash,
				ByteSize = artifact.ByteSize,
				ChunkIndex = artifact.ChunkIndex,
				ChunkCount = artifact.ChunkCount,
				StreamGroup = artifact.StreamGroup,
				MetadataJson = artifact.MetadataJson
			}).ToList(),
			TextureSummary = asset.TextureSummary is null
				? null
				: new TextureAssetSummary
				{
					RelativeSourceAssetPath = asset.TextureSummary.RelativeSourceAssetPath,
					RelativeImportedPath = asset.TextureSummary.RelativeImportedPath,
					RelativeRuntimeArtifactPath = asset.TextureSummary.RelativeRuntimeArtifactPath,
					Width = asset.TextureSummary.Width,
					Height = asset.TextureSummary.Height,
					Channels = asset.TextureSummary.Channels,
					IsSrgb = asset.TextureSummary.IsSrgb,
					SourceExtension = asset.TextureSummary.SourceExtension
				},
			MaterialSummary = asset.MaterialSummary is null
				? null
				: new MaterialAssetSummary
				{
					MaterialType = asset.MaterialSummary.MaterialType
				},
			DataAssetSummary = asset.DataAssetSummary is null
				? null
				: new DataAssetSummary
				{
					DataAssetType = asset.DataAssetSummary.DataAssetType,
					DataAssetTypeId = asset.DataAssetSummary.DataAssetTypeId,
					DisplayName = asset.DataAssetSummary.DisplayName
				},
			MeshSummary = asset.MeshSummary is null
				? null
				: new MeshAssetSummary
				{
					RelativeImportedMeshPath = asset.MeshSummary.RelativeImportedMeshPath,
					VertexCount = asset.MeshSummary.VertexCount,
					IndexCount = asset.MeshSummary.IndexCount
				},
			ModelSummary = asset.ModelSummary is null
				? null
				: new Model3DAssetSummary
				{
					RelativeImportedModelPath = asset.ModelSummary.RelativeImportedModelPath,
					RootNodeCount = asset.ModelSummary.RootNodeCount
				},
				SceneSummary = asset.SceneSummary is null
					? null
					: new SceneAssetSummary
					{
						GlobalCellPath = asset.SceneSummary.GlobalCellPath,
						SpatialCellCount = asset.SceneSummary.SpatialCellCount
					},
				PrefabSummary = asset.PrefabSummary is null
					? null
					: new PrefabAssetSummary
					{
						RootEntityId = asset.PrefabSummary.RootEntityId,
						EntityCount = asset.PrefabSummary.EntityCount
					}
			};
		}

	private void ClearUndoHistory()
	{
		_serviceProvider?.GetService<IEditorUndoRedoService>()?.Clear();
	}
}
