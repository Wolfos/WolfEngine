using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public sealed class EditorAssetInstanceRegistry : IAssetInstanceRegistry
{
	private readonly IDataAssetStore _dataAssetStore;
	private readonly IMaterialAssetStore _materialAssetStore;
	private readonly object _lock = new();
	private readonly Dictionary<Guid, object> _instances = new();
	private Dictionary<Guid, AssetDatabaseEntry> _assetsById = new();
	private string? _projectRootPath;

	public EditorAssetInstanceRegistry(
		IDataAssetStore dataAssetStore,
		IMaterialAssetStore materialAssetStore)
	{
		_dataAssetStore = dataAssetStore ?? throw new ArgumentNullException(nameof(dataAssetStore));
		_materialAssetStore = materialAssetStore ?? throw new ArgumentNullException(nameof(materialAssetStore));
	}

	public object GetInstance(Guid assetId, Type expectedType)
	{
		ArgumentNullException.ThrowIfNull(expectedType);

		lock (_lock)
		{
			EnsureProjectLoaded();

			if (_assetsById.TryGetValue(assetId, out var asset) == false)
			{
				throw new InvalidOperationException($"Asset '{assetId}' was not found in the current project.");
			}

			if (_instances.TryGetValue(assetId, out var existingInstance))
			{
				return EnsureExpectedType(assetId, expectedType, existingInstance);
			}

			var loadedInstance = LoadInstance(asset);
			_instances.Add(assetId, loadedInstance);
			return EnsureExpectedType(assetId, expectedType, loadedInstance);
		}
	}

	public void RefreshProject(string projectRootPath, AssetDatabase database)
	{
		if (string.IsNullOrWhiteSpace(projectRootPath))
		{
			throw new ArgumentException("Project root path cannot be null or empty.", nameof(projectRootPath));
		}

		ArgumentNullException.ThrowIfNull(database);

		lock (_lock)
		{
			_projectRootPath = Path.GetFullPath(projectRootPath);
			_assetsById = database.Assets
				.Select(CloneEntry)
				.ToDictionary(asset => asset.Id);
			var removedAssetIds = _instances.Keys
				.Where(assetId => _assetsById.ContainsKey(assetId) == false)
				.ToList();
			for (var i = 0; i < removedAssetIds.Count; i++)
			{
				_instances.Remove(removedAssetIds[i]);
			}
		}
	}

	public void Clear()
	{
		lock (_lock)
		{
			_projectRootPath = null;
			_assetsById = new Dictionary<Guid, AssetDatabaseEntry>();
			_instances.Clear();
		}
	}

	private void EnsureProjectLoaded()
	{
		if (string.IsNullOrWhiteSpace(_projectRootPath))
		{
			throw new InvalidOperationException("No project is currently loaded in the asset instance registry.");
		}
	}

	private object LoadInstance(AssetDatabaseEntry asset)
	{
		var absoluteAssetPath = GetAbsolutePath(asset.RelativeAssetPath);
		return asset.Type switch
		{
			AssetType.DataAsset => _dataAssetStore.LoadAsset(absoluteAssetPath).Asset,
			AssetType.Material => _materialAssetStore.LoadAsset(absoluteAssetPath),
			_ => throw new InvalidOperationException(
				$"Asset '{asset.Id}' of type '{asset.Type}' is not supported by the central asset instance registry.")
		};
	}

	private object EnsureExpectedType(Guid assetId, Type expectedType, object instance)
	{
		if (expectedType.IsInstanceOfType(instance))
		{
			return instance;
		}

		throw new InvalidOperationException(
			$"Asset '{assetId}' resolved to '{instance.GetType().FullName}', which cannot be assigned to '{expectedType.FullName}'.");
	}

	private string GetAbsolutePath(string relativePath)
	{
		EnsureProjectLoaded();
		if (string.IsNullOrWhiteSpace(relativePath))
		{
			throw new ArgumentException("Relative path cannot be null or empty.", nameof(relativePath));
		}

		var normalizedPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
		return Path.GetFullPath(Path.Combine(_projectRootPath!, normalizedPath));
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
