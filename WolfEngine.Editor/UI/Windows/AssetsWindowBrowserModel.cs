using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

internal sealed class AssetsWindowBrowserModel
{
	public required AssetsWindowFolderNode RootFolder { get; init; }
	public required IReadOnlyDictionary<string, AssetsWindowFolderNode> FoldersByPath { get; init; }
	public required IReadOnlyDictionary<Guid, AssetsWindowSourceItem> SourcesBySourceId { get; init; }
}

internal sealed class AssetsWindowFolderNode
{
	public required string RelativePath { get; init; }
	public required string Name { get; init; }
	public List<AssetsWindowFolderNode> Children { get; } = [];
	public List<AssetsWindowSourceItem> Sources { get; } = [];
}

internal sealed class AssetsWindowFolderContents
{
	public required bool IsSearchActive { get; init; }
	public required IReadOnlyList<AssetsWindowFolderNode> Folders { get; init; }
	public required IReadOnlyList<AssetsWindowSourceItem> Sources { get; init; }
}

internal sealed class AssetsWindowSourceItem
{
	public required Guid SourceId { get; init; }
	public required string RelativeSourcePath { get; init; }
	public required string DisplayName { get; init; }
	public required string FolderPath { get; init; }
	public required AssetDatabaseEntry PrimaryAsset { get; init; }
	public required IReadOnlyList<AssetDatabaseEntry> SubAssets { get; init; }
}

/// <summary>
/// Retains the browser tree until the project supplies a newer asset database or a different Assets root.
/// The cached model is treated as immutable by its consumers after construction.
/// </summary>
internal sealed class AssetsWindowBrowserModelCache
{
	private readonly Func<IReadOnlyList<AssetDatabaseEntry>, string, AssetsWindowBrowserModel> _build;
	private AssetsWindowBrowserModel? _browserModel;
	private string? _assetsRootPath;
	private long _assetDatabaseRevision;

	public AssetsWindowBrowserModelCache(
		Func<IReadOnlyList<AssetDatabaseEntry>, string, AssetsWindowBrowserModel>? build = null)
	{
		_build = build ?? AssetsWindowBrowserModelBuilder.Build;
	}

	public AssetsWindowBrowserModel GetOrBuild(
		IReadOnlyList<AssetDatabaseEntry> assets,
		string assetsRootPath,
		long assetDatabaseRevision)
	{
		ArgumentNullException.ThrowIfNull(assets);
		ArgumentException.ThrowIfNullOrWhiteSpace(assetsRootPath);

		var normalizedAssetsRootPath = Path.GetFullPath(assetsRootPath);
		if (_browserModel is not null &&
		    _assetDatabaseRevision == assetDatabaseRevision &&
		    string.Equals(_assetsRootPath, normalizedAssetsRootPath, StringComparison.OrdinalIgnoreCase))
		{
			return _browserModel;
		}

		_browserModel = _build(assets, normalizedAssetsRootPath);
		_assetsRootPath = normalizedAssetsRootPath;
		_assetDatabaseRevision = assetDatabaseRevision;
		return _browserModel;
	}

	public void Invalidate()
	{
		_browserModel = null;
		_assetsRootPath = null;
	}
}

internal static class AssetsWindowBrowserModelBuilder
{
	public static AssetsWindowBrowserModel Build(IReadOnlyList<AssetDatabaseEntry> assets, string assetsRootPath)
	{
		ArgumentNullException.ThrowIfNull(assets);
		ArgumentException.ThrowIfNullOrWhiteSpace(assetsRootPath);

		var foldersByPath = new Dictionary<string, AssetsWindowFolderNode>(StringComparer.OrdinalIgnoreCase);
		var rootFolder = EnsureFolder(AssetPipelinePaths.AssetsFolderName, foldersByPath);

		if (Directory.Exists(assetsRootPath))
		{
			foreach (var absoluteFolderPath in Directory.EnumerateDirectories(assetsRootPath, "*", SearchOption.AllDirectories))
			{
				var relativeFolderPath = Path.GetRelativePath(assetsRootPath, absoluteFolderPath);
				var normalizedRelativeFolderPath = string.IsNullOrWhiteSpace(relativeFolderPath)
					? AssetPipelinePaths.AssetsFolderName
					: ProjectPathUtility.NormalizeRelativePath($"{AssetPipelinePaths.AssetsFolderName}/{relativeFolderPath}");
				EnsureFolder(normalizedRelativeFolderPath, foldersByPath);
			}
		}

		var sourcesBySourceId = new Dictionary<Guid, AssetsWindowSourceItem>();
		foreach (var group in assets.Where(IsVisibleAsset).GroupBy(asset => asset.SourceId))
		{
			var sourceItem = CreateSourceItem(group);
			EnsureFolder(sourceItem.FolderPath, foldersByPath).Sources.Add(sourceItem);
			sourcesBySourceId[sourceItem.SourceId] = sourceItem;
		}

		SortFolder(rootFolder);
		return new AssetsWindowBrowserModel
		{
			RootFolder = rootFolder,
			FoldersByPath = foldersByPath,
			SourcesBySourceId = sourcesBySourceId
		};
	}

	public static string NormalizeSelectedFolderPath(AssetsWindowBrowserModel browserModel, string? selectedFolderPath)
	{
		ArgumentNullException.ThrowIfNull(browserModel);

		var normalizedFolderPath = ProjectPathUtility.NormalizeAssetsFolderPath(selectedFolderPath);
		while (browserModel.FoldersByPath.ContainsKey(normalizedFolderPath) == false
		       && string.Equals(normalizedFolderPath, AssetPipelinePaths.AssetsFolderName, StringComparison.OrdinalIgnoreCase) == false)
		{
			normalizedFolderPath = ProjectPathUtility.GetParentFolderPath(normalizedFolderPath);
		}

		return browserModel.FoldersByPath.ContainsKey(normalizedFolderPath)
			? normalizedFolderPath
			: AssetPipelinePaths.AssetsFolderName;
	}

	public static Guid? ToggleExpandedSource(Guid? expandedSourceId, Guid clickedSourceId)
	{
		return expandedSourceId == clickedSourceId ? null : clickedSourceId;
	}

	public static AssetsWindowFolderContents GetFolderContents(AssetsWindowFolderNode folder, string? searchText)
	{
		ArgumentNullException.ThrowIfNull(folder);

		var normalizedSearchText = searchText?.Trim();
		if (string.IsNullOrWhiteSpace(normalizedSearchText))
		{
			return new AssetsWindowFolderContents
			{
				IsSearchActive = false,
				Folders = folder.Children,
				Sources = folder.Sources
			};
		}

		var matchingFolders = new List<AssetsWindowFolderNode>();
		var matchingSources = new List<AssetsWindowSourceItem>();
		CollectMatchingContents(folder, normalizedSearchText, includeFolder: false, matchingFolders, matchingSources);
		return new AssetsWindowFolderContents
		{
			IsSearchActive = true,
			Folders = matchingFolders,
			Sources = matchingSources
		};
	}

	private static AssetsWindowSourceItem CreateSourceItem(IGrouping<Guid, AssetDatabaseEntry> group)
	{
		var groupedAssets = group
			.OrderBy(GetAssetTypeSortOrder)
			.ThenBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(asset => asset.NodeKey, StringComparer.OrdinalIgnoreCase)
			.ToList();
		var primaryAsset = groupedAssets.FirstOrDefault(asset => asset.Type == AssetType.Model3D)
			?? groupedAssets.FirstOrDefault(asset => asset.IsGenerated == false && string.Equals(asset.NodeKey, "main", StringComparison.Ordinal))
			?? groupedAssets.FirstOrDefault(asset => string.Equals(asset.NodeKey, "main", StringComparison.Ordinal))
			?? groupedAssets.FirstOrDefault(asset => asset.IsGenerated == false)
			?? groupedAssets[0];
		var subAssets = groupedAssets
			.Where(asset => asset.Id != primaryAsset.Id)
			.OrderBy(GetAssetTypeSortOrder)
			.ThenBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(asset => asset.NodeKey, StringComparer.OrdinalIgnoreCase)
			.ToList();
		var displayName = Path.GetFileName(primaryAsset.RelativeSourcePath);
		if (string.IsNullOrWhiteSpace(displayName))
		{
			displayName = primaryAsset.Name;
		}

		return new AssetsWindowSourceItem
		{
			SourceId = group.Key,
			RelativeSourcePath = primaryAsset.RelativeSourcePath,
			DisplayName = displayName,
			FolderPath = ProjectPathUtility.GetFolderPath(primaryAsset.RelativeSourcePath),
			PrimaryAsset = primaryAsset,
			SubAssets = subAssets
		};
	}

	private static AssetsWindowFolderNode EnsureFolder(string relativeFolderPath, Dictionary<string, AssetsWindowFolderNode> foldersByPath)
	{
		var normalizedFolderPath = ProjectPathUtility.NormalizeAssetsFolderPath(relativeFolderPath);
		if (foldersByPath.TryGetValue(normalizedFolderPath, out var existingFolder))
		{
			return existingFolder;
		}

		var folder = new AssetsWindowFolderNode
		{
			RelativePath = normalizedFolderPath,
			Name = string.Equals(normalizedFolderPath, AssetPipelinePaths.AssetsFolderName, StringComparison.OrdinalIgnoreCase)
				? AssetPipelinePaths.AssetsFolderName
				: Path.GetFileName(normalizedFolderPath)
		};
		foldersByPath[normalizedFolderPath] = folder;

		if (string.Equals(normalizedFolderPath, AssetPipelinePaths.AssetsFolderName, StringComparison.OrdinalIgnoreCase) == false)
		{
			var parentFolderPath = ProjectPathUtility.GetParentFolderPath(normalizedFolderPath);
			var parent = EnsureFolder(parentFolderPath, foldersByPath);
			if (parent.Children.Contains(folder) == false)
			{
				parent.Children.Add(folder);
			}
		}

		return folder;
	}

	private static void SortFolder(AssetsWindowFolderNode folder)
	{
		folder.Children.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
		folder.Sources.Sort(static (left, right) => string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase));
		for (var i = 0; i < folder.Children.Count; i++)
		{
			SortFolder(folder.Children[i]);
		}
	}

	private static bool IsVisibleAsset(AssetDatabaseEntry asset)
	{
		return asset.Type != AssetType.SceneCell;
	}

	private static void CollectMatchingContents(
		AssetsWindowFolderNode folder,
		string searchText,
		bool includeFolder,
		List<AssetsWindowFolderNode> matchingFolders,
		List<AssetsWindowSourceItem> matchingSources)
	{
		if (includeFolder && MatchesSearch(folder.Name, searchText))
		{
			matchingFolders.Add(folder);
		}

		for (var i = 0; i < folder.Sources.Count; i++)
		{
			var source = folder.Sources[i];
			if (MatchesSearch(source.DisplayName, searchText) ||
			    MatchesSearch(source.PrimaryAsset.Name, searchText))
			{
				matchingSources.Add(source);
			}
		}

		for (var i = 0; i < folder.Children.Count; i++)
		{
			CollectMatchingContents(folder.Children[i], searchText, includeFolder: true, matchingFolders, matchingSources);
		}
	}

	private static bool MatchesSearch(string value, string searchText)
	{
		return value.Contains(searchText, StringComparison.OrdinalIgnoreCase);
	}

	private static int GetAssetTypeSortOrder(AssetDatabaseEntry asset)
	{
		return asset.Type switch
		{
			AssetType.Scene => 0,
			AssetType.Prefab => 1,
			AssetType.Model3D => 2,
			AssetType.Mesh => 3,
			AssetType.Material => 4,
			AssetType.Terrain => 5,
			AssetType.Texture2D => 6,
			AssetType.AudioClip => 7,
			AssetType.DataAsset => 8,
			_ => 10
		};
	}
}
