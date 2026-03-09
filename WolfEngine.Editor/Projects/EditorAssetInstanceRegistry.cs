using Microsoft.Extensions.DependencyInjection;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public sealed class EditorAssetInstanceRegistry : IAssetInstanceRegistry
{
	private readonly IServiceProvider _serviceProvider;
	private readonly object _lock = new();
	private readonly Dictionary<Guid, object> _instances = new();
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

			if (_instances.TryGetValue(assetId, out var existingInstance))
			{
				return EnsureExpectedType(assetId, expectedType, existingInstance);
			}

			var loadedInstance = LoadInstance(asset, expectedType);
			if (loadedInstance is not null)
			{
				_instances.Add(assetId, loadedInstance);
			}

			return loadedInstance is null
				? null
				: EnsureExpectedType(assetId, expectedType, loadedInstance);
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

	private object? LoadInstance(AssetDatabaseEntry asset, Type expectedType)
	{
		var descriptor = RuntimeAssetDescriptor.Get(expectedType);
		if (descriptor.AssetType != asset.Type)
		{
			throw new InvalidOperationException(
				$"Asset '{asset.Id}' is registered as '{asset.Type}' and cannot be resolved as '{expectedType.FullName}'.");
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
		if (string.IsNullOrWhiteSpace(_projectRootPath))
		{
			throw new InvalidOperationException("No project is currently loaded in the asset instance registry.");
		}

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
			RelativeStatePath = asset.RelativeStatePath,
			RelativeMetaPath = asset.RelativeMetaPath,
			TextureSummary = asset.TextureSummary is null
				? null
				: new TextureAssetSummary
				{
					RelativeSourceAssetPath = asset.TextureSummary.RelativeSourceAssetPath,
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
