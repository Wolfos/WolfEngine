using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using WolfEngine.AssetPipeline;
using WolfEngine.Importing;
using WolfEngine.Editor;
using WolfEngine.Editor.UI;
using WolfEngine.Rendering.Shaders;

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
	long AssetDatabaseRevision => 0;

	bool CreateProject(string parentFolder, string projectName, out string errorMessage);
	bool OpenProject(string projectRoot, out string errorMessage);
	void CloseProject();
	AssetDatabaseRefreshResult ReloadAssetDatabase();
	void ReloadAssetDatabaseFromIndex();
	void RefreshAssetSource(string relativeSourcePath);
	void RefreshAssetSource(string relativeSourcePath, Guid preservedRuntimeAssetId) => RefreshAssetSource(relativeSourcePath);
	void SaveAssetDatabase(AssetDatabase database);
	AssetDatabase CloneCurrentAssetDatabase();
	bool TryGetAsset(Guid assetId, out AssetDatabaseEntry asset);
	string GetAbsolutePath(string relativePath);
	void DeleteAssetSource(string relativeSourcePath);
	void DeleteFolder(string relativeFolderPath);
	string RenameAssetSource(string relativeSourcePath, string newName);
	string RenameFolder(string relativeFolderPath, string newName);
	string MoveAssetSourceToFolder(string relativeSourcePath, string targetFolderPath);
	string MoveFolderToFolder(string relativeFolderPath, string targetFolderPath);
	string CreateFolder(string parentFolderPath, string folderName);
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
	private readonly IShaderProvider? _shaderProvider;
	private AssetDatabase _currentAssetDatabase = new();
	private long _assetDatabaseRevision;
	private string? _projectRootPath;
	private EditorProjectManifest? _projectManifest;

	public EditorProjectService(
		IProjectAssetPipelineService assetPipelineService,
		IAssetInstanceRegistry assetInstanceRegistry,
		IEditorNotificationService? notificationService = null,
		IServiceProvider? serviceProvider = null,
		IShaderProvider? shaderProvider = null)
	{
		_assetPipelineService = assetPipelineService ?? throw new ArgumentNullException(nameof(assetPipelineService));
		_assetInstanceRegistry = assetInstanceRegistry ?? throw new ArgumentNullException(nameof(assetInstanceRegistry));
		_notificationService = notificationService;
		_serviceProvider = serviceProvider;
		_shaderProvider = shaderProvider;
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
	public long AssetDatabaseRevision => _assetDatabaseRevision;

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
			_shaderProvider?.SetProjectRoot(_projectRootPath);
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
			_shaderProvider?.SetProjectRoot(null);
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
		_shaderProvider?.SetProjectRoot(null);
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
		var refresh = _assetPipelineService.RefreshProjectIncrementalWithChanges(_projectRootPath!);
		var refreshedDatabase = refresh.Database;
		ApplyDatabase(refreshedDatabase);

		var changedNodeIds = CollectChangedNodeIds(previousDatabase, refreshedDatabase).ToHashSet();
		changedNodeIds.UnionWith(refresh.ReimportedNodeIds);
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
		RefreshAssetSource(relativeSourcePath, Guid.Empty);
	}

	public void RefreshAssetSource(string relativeSourcePath, Guid preservedRuntimeAssetId)
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
		if (preservedRuntimeAssetId != Guid.Empty)
		{
			invalidatedNodeIds = invalidatedNodeIds
				.Where(assetId => assetId != preservedRuntimeAssetId)
				.ToArray();
		}
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

	public string RenameAssetSource(string relativeSourcePath, string newName)
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

		var sourceName = Path.GetFileName(normalizedRelativePath);
		if (string.IsNullOrWhiteSpace(sourceName))
		{
			throw new InvalidOperationException($"Asset source '{normalizedRelativePath}' was not found.");
		}

		var absoluteSourcePath = GetAbsolutePath(normalizedRelativePath);
		var absoluteMetaPath = AssetFileExtensions.GetMetaPath(absoluteSourcePath);
		if (File.Exists(absoluteSourcePath) == false)
		{
			throw new FileNotFoundException($"Asset source '{normalizedRelativePath}' was not found.");
		}

		var suffix = GetAssetSourceSuffix(sourceName);
		var validatedName = ValidateNewFileSystemName(newName, "Asset name");
		var newSourceName = validatedName + suffix;
		var parentPath = ProjectPathUtility.GetFolderPath(normalizedRelativePath);
		var newRelativePath = ProjectPathUtility.NormalizeRelativePath($"{parentPath}/{newSourceName}");
		EnsureAssetsDescendantTarget(newRelativePath);

		if (string.Equals(normalizedRelativePath, newRelativePath, StringComparison.Ordinal))
		{
			return normalizedRelativePath;
		}

		var targetSourcePath = GetAbsolutePath(newRelativePath);
		var targetMetaPath = AssetFileExtensions.GetMetaPath(targetSourcePath);
		if (File.Exists(targetMetaPath) &&
		    string.Equals(absoluteMetaPath, targetMetaPath, StringComparison.OrdinalIgnoreCase) == false)
		{
			throw new IOException($"Asset metadata '{targetMetaPath}' already exists.");
		}

		MoveFileNoOverwrite(absoluteSourcePath, targetSourcePath);
		if (File.Exists(absoluteMetaPath))
		{
			MoveFileNoOverwrite(absoluteMetaPath, targetMetaPath);
		}

		_assetPipelineService.RemoveDeletedSource(_projectRootPath!, normalizedRelativePath);
		_assetPipelineService.ReimportSource(_projectRootPath!, newRelativePath);
		ReloadAssetDatabaseFromIndex();
		return newRelativePath;
	}

	public string RenameFolder(string relativeFolderPath, string newName)
	{
		if (HasOpenProject == false)
		{
			throw new InvalidOperationException("No project is currently open.");
		}

		var normalizedRelativePath = ProjectPathUtility.NormalizeAssetsFolderPath(relativeFolderPath);
		if (string.Equals(normalizedRelativePath, AssetPipelinePaths.AssetsFolderName, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("Cannot rename the root Assets folder.");
		}

		var absoluteFolderPath = GetAbsolutePath(normalizedRelativePath);
		if (Directory.Exists(absoluteFolderPath) == false)
		{
			throw new DirectoryNotFoundException($"Folder '{normalizedRelativePath}' was not found.");
		}

		var validatedName = ValidateNewFileSystemName(newName, "Folder name");
		var parentPath = ProjectPathUtility.GetParentFolderPath(normalizedRelativePath);
		var newRelativePath = ProjectPathUtility.NormalizeRelativePath($"{parentPath}/{validatedName}");
		EnsureAssetsDescendantTarget(newRelativePath);

		if (string.Equals(normalizedRelativePath, newRelativePath, StringComparison.Ordinal))
		{
			return normalizedRelativePath;
		}

		var oldPrefix = normalizedRelativePath + "/";
		var movedSources = _currentAssetDatabase.Assets
			.Select(asset => asset.RelativeSourcePath)
			.Where(path => path.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Select(path => new
			{
				OldPath = path,
				NewPath = ProjectPathUtility.NormalizeRelativePath(newRelativePath + "/" + path[oldPrefix.Length..])
			})
			.ToList();

		MoveDirectoryNoOverwrite(absoluteFolderPath, GetAbsolutePath(newRelativePath));
		for (var i = 0; i < movedSources.Count; i++)
		{
			_assetPipelineService.RemoveDeletedSource(_projectRootPath!, movedSources[i].OldPath);
			_assetPipelineService.ReimportSource(_projectRootPath!, movedSources[i].NewPath);
		}

		ReloadAssetDatabaseFromIndex();
		return newRelativePath;
	}

	public string MoveAssetSourceToFolder(string relativeSourcePath, string targetFolderPath)
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

		var normalizedTargetFolderPath = ProjectPathUtility.NormalizeAssetsFolderPath(targetFolderPath);
		var absoluteTargetFolderPath = GetAbsolutePath(normalizedTargetFolderPath);
		if (Directory.Exists(absoluteTargetFolderPath) == false)
		{
			throw new DirectoryNotFoundException($"Folder '{normalizedTargetFolderPath}' was not found.");
		}

		var sourceName = Path.GetFileName(normalizedRelativePath);
		if (string.IsNullOrWhiteSpace(sourceName))
		{
			throw new InvalidOperationException($"Asset source '{normalizedRelativePath}' was not found.");
		}

		var currentFolderPath = ProjectPathUtility.GetFolderPath(normalizedRelativePath);
		if (string.Equals(currentFolderPath, normalizedTargetFolderPath, StringComparison.OrdinalIgnoreCase))
		{
			return normalizedRelativePath;
		}

		var absoluteSourcePath = GetAbsolutePath(normalizedRelativePath);
		var absoluteMetaPath = AssetFileExtensions.GetMetaPath(absoluteSourcePath);
		if (File.Exists(absoluteSourcePath) == false)
		{
			throw new FileNotFoundException($"Asset source '{normalizedRelativePath}' was not found.");
		}

		var newRelativePath = ProjectPathUtility.NormalizeRelativePath($"{normalizedTargetFolderPath}/{sourceName}");
		EnsureAssetsDescendantTarget(newRelativePath);
		var targetSourcePath = GetAbsolutePath(newRelativePath);
		var targetMetaPath = AssetFileExtensions.GetMetaPath(targetSourcePath);
		if (File.Exists(targetMetaPath) &&
		    string.Equals(absoluteMetaPath, targetMetaPath, StringComparison.OrdinalIgnoreCase) == false)
		{
			throw new IOException($"Asset metadata '{targetMetaPath}' already exists.");
		}

		MoveFileNoOverwrite(absoluteSourcePath, targetSourcePath);
		if (File.Exists(absoluteMetaPath))
		{
			MoveFileNoOverwrite(absoluteMetaPath, targetMetaPath);
		}

		_assetPipelineService.RemoveDeletedSource(_projectRootPath!, normalizedRelativePath);
		_assetPipelineService.ReimportSource(_projectRootPath!, newRelativePath);
		ReloadAssetDatabaseFromIndex();
		return newRelativePath;
	}

	public string MoveFolderToFolder(string relativeFolderPath, string targetFolderPath)
	{
		if (HasOpenProject == false)
		{
			throw new InvalidOperationException("No project is currently open.");
		}

		var normalizedRelativePath = ProjectPathUtility.NormalizeAssetsFolderPath(relativeFolderPath);
		if (string.Equals(normalizedRelativePath, AssetPipelinePaths.AssetsFolderName, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("Cannot move the root Assets folder.");
		}

		var normalizedTargetFolderPath = ProjectPathUtility.NormalizeAssetsFolderPath(targetFolderPath);
		if (ProjectPathUtility.IsSameOrDescendant(normalizedTargetFolderPath, normalizedRelativePath))
		{
			throw new InvalidOperationException("Cannot move a folder into itself or one of its descendants.");
		}

		var absoluteFolderPath = GetAbsolutePath(normalizedRelativePath);
		if (Directory.Exists(absoluteFolderPath) == false)
		{
			throw new DirectoryNotFoundException($"Folder '{normalizedRelativePath}' was not found.");
		}

		var absoluteTargetFolderPath = GetAbsolutePath(normalizedTargetFolderPath);
		if (Directory.Exists(absoluteTargetFolderPath) == false)
		{
			throw new DirectoryNotFoundException($"Folder '{normalizedTargetFolderPath}' was not found.");
		}

		var currentParentPath = ProjectPathUtility.GetParentFolderPath(normalizedRelativePath);
		if (string.Equals(currentParentPath, normalizedTargetFolderPath, StringComparison.OrdinalIgnoreCase))
		{
			return normalizedRelativePath;
		}

		var folderName = Path.GetFileName(normalizedRelativePath);
		var newRelativePath = ProjectPathUtility.NormalizeRelativePath($"{normalizedTargetFolderPath}/{folderName}");
		EnsureAssetsDescendantTarget(newRelativePath);

		var movedSources = CollectMovedSources(normalizedRelativePath, newRelativePath);
		MoveDirectoryNoOverwrite(absoluteFolderPath, GetAbsolutePath(newRelativePath));
		for (var i = 0; i < movedSources.Count; i++)
		{
			_assetPipelineService.RemoveDeletedSource(_projectRootPath!, movedSources[i].OldPath);
			_assetPipelineService.ReimportSource(_projectRootPath!, movedSources[i].NewPath);
		}

		ReloadAssetDatabaseFromIndex();
		return newRelativePath;
	}

	public string CreateFolder(string parentFolderPath, string folderName)
	{
		if (HasOpenProject == false)
		{
			throw new InvalidOperationException("No project is currently open.");
		}

		var normalizedParentPath = ProjectPathUtility.NormalizeAssetsFolderPath(parentFolderPath);
		var absoluteParentPath = GetAbsolutePath(normalizedParentPath);
		if (Directory.Exists(absoluteParentPath) == false)
		{
			throw new DirectoryNotFoundException($"Folder '{normalizedParentPath}' was not found.");
		}

		var validatedName = ValidateNewFileSystemName(folderName, "Folder name");
		var relativeFolderPath = ProjectPathUtility.NormalizeRelativePath($"{normalizedParentPath}/{validatedName}");
		EnsureAssetsDescendantTarget(relativeFolderPath);
		var absoluteFolderPath = GetAbsolutePath(relativeFolderPath);
		if (File.Exists(absoluteFolderPath))
		{
			throw new IOException($"File '{absoluteFolderPath}' already exists.");
		}

		if (Directory.Exists(absoluteFolderPath))
		{
			throw new IOException($"Folder '{absoluteFolderPath}' already exists.");
		}

		Directory.CreateDirectory(absoluteFolderPath);
		return relativeFolderPath;
	}

	private List<(string OldPath, string NewPath)> CollectMovedSources(string oldFolderPath, string newFolderPath)
	{
		var oldPrefix = oldFolderPath + "/";
		return _currentAssetDatabase.Assets
			.Select(asset => asset.RelativeSourcePath)
			.Where(path => path.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Select(path => (
				OldPath: path,
				NewPath: ProjectPathUtility.NormalizeRelativePath(newFolderPath + "/" + path[oldPrefix.Length..])))
			.ToList();
	}

	private static string GetAssetSourceSuffix(string fileName)
	{
		string[] compoundSuffixes =
		[
			MaterialAsset.FileExtension,
			DataAssetFile.FileExtension,
			EditorSceneAssetFile.FileExtension,
			PrefabAssetFile.FileExtension
		];

		for (var i = 0; i < compoundSuffixes.Length; i++)
		{
			if (fileName.EndsWith(compoundSuffixes[i], StringComparison.OrdinalIgnoreCase))
			{
				return fileName[^compoundSuffixes[i].Length..];
			}
		}

		return Path.GetExtension(fileName);
	}

	private static string ValidateNewFileSystemName(string newName, string label)
	{
		var trimmedName = newName.Trim();
		if (string.IsNullOrWhiteSpace(trimmedName))
		{
			throw new InvalidOperationException($"{label} cannot be empty.");
		}

		if (string.Equals(trimmedName, ".", StringComparison.Ordinal) ||
		    string.Equals(trimmedName, "..", StringComparison.Ordinal))
		{
			throw new InvalidOperationException($"{label} cannot be '.' or '..'.");
		}

		if (trimmedName.Contains('/') || trimmedName.Contains('\\'))
		{
			throw new InvalidOperationException($"{label} cannot contain path separators.");
		}

		if (trimmedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
		{
			throw new InvalidOperationException($"{label} contains invalid characters.");
		}

		return trimmedName;
	}

	private static void EnsureAssetsDescendantTarget(string relativePath)
	{
		if (ProjectPathUtility.IsAssetsPathOrDescendant(relativePath) == false)
		{
			throw new InvalidOperationException($"Path '{relativePath}' must be inside the Assets folder.");
		}
	}

	private static void MoveFileNoOverwrite(string sourcePath, string targetPath)
	{
		var targetDirectory = Path.GetDirectoryName(targetPath);
		if (string.IsNullOrWhiteSpace(targetDirectory) == false)
		{
			Directory.CreateDirectory(targetDirectory);
		}

		if (File.Exists(targetPath) &&
		    string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase) == false)
		{
			throw new IOException($"File '{targetPath}' already exists.");
		}

		if (Directory.Exists(targetPath))
		{
			throw new IOException($"Folder '{targetPath}' already exists.");
		}

		if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase) &&
		    string.Equals(sourcePath, targetPath, StringComparison.Ordinal) == false)
		{
			var tempPath = CreateTemporarySiblingPath(sourcePath);
			File.Move(sourcePath, tempPath);
			File.Move(tempPath, targetPath);
			return;
		}

		File.Move(sourcePath, targetPath, overwrite: false);
	}

	private static void MoveDirectoryNoOverwrite(string sourcePath, string targetPath)
	{
		var targetParent = Path.GetDirectoryName(targetPath);
		if (string.IsNullOrWhiteSpace(targetParent) == false)
		{
			Directory.CreateDirectory(targetParent);
		}

		if (Directory.Exists(targetPath) &&
		    string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase) == false)
		{
			throw new IOException($"Folder '{targetPath}' already exists.");
		}

		if (File.Exists(targetPath))
		{
			throw new IOException($"File '{targetPath}' already exists.");
		}

		if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase) &&
		    string.Equals(sourcePath, targetPath, StringComparison.Ordinal) == false)
		{
			var tempPath = CreateTemporarySiblingPath(sourcePath);
			Directory.Move(sourcePath, tempPath);
			Directory.Move(tempPath, targetPath);
			return;
		}

		Directory.Move(sourcePath, targetPath);
	}

	private static string CreateTemporarySiblingPath(string path)
	{
		var directory = Path.GetDirectoryName(path) ?? string.Empty;
		var fileName = Path.GetFileName(path);
		string candidate;
		do
		{
			candidate = Path.Combine(directory, $".{fileName}.rename-{Guid.NewGuid():N}.tmp");
		} while (File.Exists(candidate) || Directory.Exists(candidate));

		return candidate;
	}

	private void ApplyDatabase(AssetDatabase database)
	{
		ArgumentNullException.ThrowIfNull(database);
		_currentAssetDatabase = database;
		_assetDatabaseRevision++;
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
		       string.Equals(left.SummaryJson, right.SummaryJson, StringComparison.Ordinal) &&
		       ArtifactListsEqual(left.Artifacts, right.Artifacts);
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
			SummaryJson = asset.SummaryJson,
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
			}).ToList()
			};
		}

	private void ClearUndoHistory()
	{
		_serviceProvider?.GetService<IEditorUndoRedoService>()?.Clear();
	}
}
