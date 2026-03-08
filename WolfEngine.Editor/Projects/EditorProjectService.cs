using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public interface IEditorProjectService
{
	bool HasOpenProject { get; }
	string? ProjectRootPath { get; }
	string? AssetsPath { get; }
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
	private readonly IAssetDatabaseStore _assetDatabaseStore;
	private AssetDatabase _currentAssetDatabase;
	private string? _projectRootPath;

	public EditorProjectService(IAssetDatabaseStore assetDatabaseStore)
	{
		_assetDatabaseStore = assetDatabaseStore ?? throw new ArgumentNullException(nameof(assetDatabaseStore));
		_currentAssetDatabase = _assetDatabaseStore.CreateEmpty();
	}

	public bool HasOpenProject => string.IsNullOrWhiteSpace(_projectRootPath) == false;
	public string? ProjectRootPath => _projectRootPath;
	public string? AssetsPath => HasOpenProject ? Path.Combine(_projectRootPath!, "Assets") : null;
	public string? DatabasePath => HasOpenProject ? Path.Combine(_projectRootPath!, "Database") : null;
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

		var assetsPath = Path.Combine(projectRoot, "Assets");
		var databasePath = Path.Combine(projectRoot, "Database");
		var databaseFilePath = Path.Combine(databasePath, AssetDatabase.FileName);

		try
		{
			Directory.CreateDirectory(assetsPath);
			Directory.CreateDirectory(databasePath);
			_assetDatabaseStore.Save(databaseFilePath, _assetDatabaseStore.CreateEmpty());
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
		var assetsPath = Path.Combine(fullProjectRoot, "Assets");
		var databasePath = Path.Combine(fullProjectRoot, "Database");
		var databaseFilePath = Path.Combine(databasePath, AssetDatabase.FileName);

		if (Directory.Exists(fullProjectRoot) == false)
		{
			errorMessage = $"Project folder '{fullProjectRoot}' does not exist.";
			return false;
		}

		if (Directory.Exists(assetsPath) == false || Directory.Exists(databasePath) == false)
		{
			errorMessage = "Project folder must contain both Assets and Database subfolders.";
			return false;
		}

		if (File.Exists(databaseFilePath) == false)
		{
			errorMessage = $"Project database '{databaseFilePath}' was not found.";
			return false;
		}

		AssetDatabase loadedDatabase;
		try
		{
			loadedDatabase = _assetDatabaseStore.Load(databaseFilePath);
		}
		catch (Exception ex)
		{
			errorMessage = $"Failed to open project: {ex.Message}";
			return false;
		}

		_projectRootPath = fullProjectRoot;
		_currentAssetDatabase = loadedDatabase;
		return true;
	}

	public void CloseProject()
	{
		_projectRootPath = null;
		_currentAssetDatabase = _assetDatabaseStore.CreateEmpty();
	}

	public void ReloadAssetDatabase()
	{
		if (HasOpenProject == false)
		{
			_currentAssetDatabase = _assetDatabaseStore.CreateEmpty();
			return;
		}

		_currentAssetDatabase = _assetDatabaseStore.Load(Path.Combine(DatabasePath!, AssetDatabase.FileName));
	}

	public void SaveAssetDatabase(AssetDatabase database)
	{
		if (HasOpenProject == false)
		{
			throw new InvalidOperationException("No project is currently open.");
		}

		ArgumentNullException.ThrowIfNull(database);
		var databaseFilePath = Path.Combine(DatabasePath!, AssetDatabase.FileName);
		_assetDatabaseStore.Save(databaseFilePath, database);
		_currentAssetDatabase = CloneAssetDatabase(database);
	}

	public AssetDatabase CloneCurrentAssetDatabase()
	{
		return CloneAssetDatabase(_currentAssetDatabase);
	}

	public bool TryGetAsset(Guid assetId, out AssetDatabaseEntry asset)
	{
		for (var i = 0; i < _currentAssetDatabase.Assets.Count; i++)
		{
			var candidate = _currentAssetDatabase.Assets[i];
			if (candidate.Id == assetId)
			{
				asset = candidate;
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

	private static AssetDatabase CloneAssetDatabase(AssetDatabase source)
	{
		return new AssetDatabase
		{
			Version = source.Version,
			Assets = source.Assets.Select(CloneEntry).ToList()
		};
	}

	private static AssetDatabaseEntry CloneEntry(AssetDatabaseEntry asset)
	{
		return new AssetDatabaseEntry
		{
			Id = asset.Id,
			Type = asset.Type,
			Name = asset.Name,
			RelativeAssetPath = asset.RelativeAssetPath,
			RelativeMetaPath = asset.RelativeMetaPath,
			TextureSummary = asset.TextureSummary is null
				? null
				: new TextureAssetSummary
				{
					RelativeRawImagePath = asset.TextureSummary.RelativeRawImagePath,
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
				}
		};
	}
}
