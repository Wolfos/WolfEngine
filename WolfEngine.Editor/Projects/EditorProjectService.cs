using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public interface IEditorProjectService
{
	bool HasOpenProject { get; }
	string? ProjectRootPath { get; }
	string? AssetsPath { get; }
	string? LibraryPath { get; }
	string? DatabasePath { get; }
	AssetDatabase CurrentAssetDatabase { get; }

	bool CreateProject(string parentFolder, string projectName, out string errorMessage);
	bool OpenProject(string projectRoot, out string errorMessage);
	void CloseProject();
	void ReloadAssetDatabase();
	void SaveAssetDatabase(AssetDatabase database);
	AssetDatabase CloneCurrentAssetDatabase();
	bool TryGetAsset(Guid assetId, out AssetDatabaseEntry asset);
	string GetAbsolutePath(string relativePath);
}

public sealed class EditorProjectService : IEditorProjectService
{
	private readonly IProjectAssetPipelineService _assetPipelineService;
	private readonly IAssetInstanceRegistry _assetInstanceRegistry;
	private AssetDatabase _currentAssetDatabase = new();
	private string? _projectRootPath;

	public EditorProjectService(IProjectAssetPipelineService assetPipelineService, IAssetInstanceRegistry assetInstanceRegistry)
	{
		_assetPipelineService = assetPipelineService ?? throw new ArgumentNullException(nameof(assetPipelineService));
		_assetInstanceRegistry = assetInstanceRegistry ?? throw new ArgumentNullException(nameof(assetInstanceRegistry));
		_assetInstanceRegistry.Clear();
	}

	public bool HasOpenProject => string.IsNullOrWhiteSpace(_projectRootPath) == false;
	public string? ProjectRootPath => _projectRootPath;
	public string? AssetsPath => HasOpenProject ? AssetPipelinePaths.GetAssetsPath(_projectRootPath!) : null;
	public string? LibraryPath => HasOpenProject ? AssetPipelinePaths.GetLibraryPath(_projectRootPath!) : null;
	public string? DatabasePath => LibraryPath;
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

		if (Directory.Exists(assetsPath) == false || Directory.Exists(libraryPath) == false)
		{
			errorMessage = "Project folder must contain both Assets and Library subfolders.";
			return false;
		}

		try
		{
			_projectRootPath = fullProjectRoot;
			_currentAssetDatabase = _assetPipelineService.RefreshProject(_projectRootPath);
			_assetInstanceRegistry.Clear();
			_assetInstanceRegistry.RefreshProject(_projectRootPath, CloneCurrentAssetDatabase());
			return true;
		}
		catch (Exception ex)
		{
			_projectRootPath = null;
			_currentAssetDatabase = new AssetDatabase();
			_assetInstanceRegistry.Clear();
			errorMessage = $"Failed to open project: {ex.Message}";
			return false;
		}
	}

	public void CloseProject()
	{
		_projectRootPath = null;
		_currentAssetDatabase = new AssetDatabase();
		_assetInstanceRegistry.Clear();
	}

	public void ReloadAssetDatabase()
	{
		if (HasOpenProject == false)
		{
			_currentAssetDatabase = new AssetDatabase();
			_assetInstanceRegistry.Clear();
			return;
		}

		_currentAssetDatabase = _assetPipelineService.RefreshProject(_projectRootPath!);
		_assetInstanceRegistry.RefreshProject(_projectRootPath!, CloneCurrentAssetDatabase());
	}

	public void SaveAssetDatabase(AssetDatabase database)
	{
		ArgumentNullException.ThrowIfNull(database);
		ReloadAssetDatabase();
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
				}
		};
	}
}
