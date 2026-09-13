using Microsoft.Extensions.DependencyInjection;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public sealed class EditorAssetInstanceRegistry : IAssetInstanceRegistry
{
	private readonly IServiceProvider _serviceProvider;
	private readonly object _lock = new();
	private readonly Dictionary<(Guid NodeId, Type RuntimeType), object> _instances = new();
	private readonly HashSet<(Guid NodeId, Type RuntimeType)> _inProgress = new();
	private Dictionary<Guid, MountedAsset> _assetsById = new();
	private IAssetCatalog? _catalog;

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
			if (_catalog is null)
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
		RefreshCatalog(new AssetCatalog([
			new DirectoryAssetMount("project", "Project", projectRootPath, false, database)
		]));
	}

	public void RefreshCatalog(IAssetCatalog catalog)
	{
		ArgumentNullException.ThrowIfNull(catalog);

		lock (_lock)
		{
			var previousAssets = _assetsById;
			_catalog = catalog;
			_assetsById = catalog.Assets.ToDictionary(
				mounted => mounted.Asset.Id,
				mounted => new MountedAsset(mounted.Mount, CloneEntry(mounted.Asset)));
			var staleKeys = _instances.Keys.Where(key =>
				!_assetsById.TryGetValue(key.NodeId, out var current) ||
				!previousAssets.TryGetValue(key.NodeId, out var previous) ||
				!MountedAssetsEquivalent(previous, current)).ToList();
			for (var i = 0; i < staleKeys.Count; i++)
			{
				_instances.Remove(staleKeys[i]);
			}

			_inProgress.Clear();
		}
	}

	private static bool MountedAssetsEquivalent(MountedAsset left, MountedAsset right)
	{
		var a = left.Asset;
		var b = right.Asset;
		return string.Equals(left.Mount.Id, right.Mount.Id, StringComparison.Ordinal) &&
		       string.Equals(left.Mount.RootPath, right.Mount.RootPath, StringComparison.Ordinal) &&
		       a.Type == b.Type &&
		       string.Equals(a.RelativeAssetPath, b.RelativeAssetPath, StringComparison.Ordinal) &&
		       string.Equals(a.SummaryJson, b.SummaryJson, StringComparison.Ordinal) &&
		       a.Artifacts.Count == b.Artifacts.Count &&
		       a.Artifacts.Zip(b.Artifacts).All(pair =>
			       string.Equals(pair.First.RelativePath, pair.Second.RelativePath, StringComparison.Ordinal) &&
			       string.Equals(pair.First.ContentHash, pair.Second.ContentHash, StringComparison.Ordinal));
	}

	public void InvalidateAssets(IEnumerable<Guid> assetIds)
	{
		ArgumentNullException.ThrowIfNull(assetIds);

		lock (_lock)
		{
			var nodeIds = assetIds
				.Where(assetId => assetId != Guid.Empty)
				.ToHashSet();
			if (nodeIds.Count == 0)
			{
				return;
			}

			var staleKeys = _instances.Keys.Where(key => nodeIds.Contains(key.NodeId)).ToList();
			for (var i = 0; i < staleKeys.Count; i++)
			{
				_instances.Remove(staleKeys[i]);
			}

			var staleInProgressKeys = _inProgress.Where(key => nodeIds.Contains(key.NodeId)).ToList();
			for (var i = 0; i < staleInProgressKeys.Count; i++)
			{
				_inProgress.Remove(staleInProgressKeys[i]);
			}
		}
	}

	public void Clear()
	{
		lock (_lock)
		{
			_catalog = null;
			_assetsById = new Dictionary<Guid, MountedAsset>();
			_instances.Clear();
			_inProgress.Clear();
		}
	}

	public void ClearCachedInstances()
	{
		lock (_lock)
		{
			_instances.Clear();
			_inProgress.Clear();
		}
	}

	private object? LoadInstance(MountedAsset mountedAsset, Type expectedType)
	{
		var asset = mountedAsset.Asset;
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
			mountedAsset.Mount,
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
}
