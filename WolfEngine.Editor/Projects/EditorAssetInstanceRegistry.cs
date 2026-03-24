using Microsoft.Extensions.DependencyInjection;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public sealed class EditorAssetInstanceRegistry : IAssetInstanceRegistry
{
	private readonly IServiceProvider _serviceProvider;
	private readonly object _lock = new();
	private readonly Dictionary<(Guid NodeId, Type RuntimeType), object> _instances = new();
	private readonly HashSet<(Guid NodeId, Type RuntimeType)> _inProgress = new();
	private Dictionary<Guid, AssetDatabaseEntry> _assetsById = new();
	private string? _projectRootPath;

	public EditorAssetInstanceRegistry(IServiceProvider serviceProvider)
	{
		_serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
	}

	public object? GetInstance(Guid assetId, Type expectedType)
	{
		ArgumentNullException.ThrowIfNull(expectedType);
		if (assetId == Guid.Empty)
		{
			return null;
		}

		lock (_lock)
		{
			if (string.IsNullOrWhiteSpace(_projectRootPath))
			{
				return null;
			}

			if (_assetsById.TryGetValue(assetId, out var asset) == false)
			{
				return null;
			}

			var cacheKey = (assetId, expectedType);
			if (_instances.TryGetValue(cacheKey, out var existingInstance))
			{
				return EnsureExpectedType(assetId, expectedType, existingInstance);
			}

			if (_inProgress.Contains(cacheKey))
			{
				throw new InvalidOperationException(
					$"Detected a cyclic or re-entrant asset resolution for node '{assetId}' and runtime type '{expectedType.FullName}'.");
			}

			_inProgress.Add(cacheKey);
			try
			{
				var loadedInstance = LoadInstance(asset, expectedType);
				if (loadedInstance is not null)
				{
					_instances[cacheKey] = loadedInstance;
				}

				return loadedInstance is null
					? null
					: EnsureExpectedType(assetId, expectedType, loadedInstance);
			}
			finally
			{
				_inProgress.Remove(cacheKey);
			}
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
			_assetsById = database.Assets.ToDictionary(asset => asset.Id, CloneEntry);
			var validNodeIds = _assetsById.Keys.ToHashSet();
			var staleKeys = _instances.Keys.Where(key => validNodeIds.Contains(key.NodeId) == false).ToList();
			for (var i = 0; i < staleKeys.Count; i++)
			{
				_instances.Remove(staleKeys[i]);
			}

			_inProgress.Clear();
		}
	}

	public void Clear()
	{
		lock (_lock)
		{
			_projectRootPath = null;
			_assetsById = new Dictionary<Guid, AssetDatabaseEntry>();
			_instances.Clear();
			_inProgress.Clear();
		}
	}

	private object? LoadInstance(AssetDatabaseEntry asset, Type expectedType)
	{
		var descriptor = RuntimeAssetDescriptor.Get(expectedType);
		if (descriptor.AssetType != asset.Type)
		{
			throw new InvalidOperationException(
				$"Asset node '{asset.Id}' is registered as '{asset.Type}' and cannot be resolved as '{expectedType.FullName}'.");
		}

		if (typeof(IRuntimeAssetResolver).IsAssignableFrom(descriptor.ResolverType) == false)
		{
			throw new InvalidOperationException(
				$"Resolver type '{descriptor.ResolverType.FullName}' for '{expectedType.FullName}' does not implement IRuntimeAssetResolver.");
		}

		var resolver = (IRuntimeAssetResolver)_serviceProvider.GetRequiredService(descriptor.ResolverType);
		return resolver.Resolve(new RuntimeAssetResolveContext(
			asset.Id,
			asset,
			expectedType,
			_projectRootPath ?? throw new InvalidOperationException("No project is currently loaded in the asset instance registry."),
			ResolveReferencedAsset));
	}

	private object? ResolveReferencedAsset(Guid assetId, Type expectedType)
	{
		return GetInstance(assetId, expectedType);
	}

	private static object EnsureExpectedType(Guid assetId, Type expectedType, object instance)
	{
		if (expectedType.IsInstanceOfType(instance))
		{
			return instance;
		}

		throw new InvalidOperationException(
			$"Asset node '{assetId}' resolved to '{instance.GetType().FullName}', which cannot be assigned to '{expectedType.FullName}'.");
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
			TextureSummary = asset.TextureSummary,
			MaterialSummary = asset.MaterialSummary,
			DataAssetSummary = asset.DataAssetSummary,
			MeshSummary = asset.MeshSummary,
			ModelSummary = asset.ModelSummary
		};
	}
}
