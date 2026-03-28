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

internal sealed class AssetsWindowSourceItem
{
	public required Guid SourceId { get; init; }
	public required string RelativeSourcePath { get; init; }
	public required string DisplayName { get; init; }
	public required string FolderPath { get; init; }
	public required AssetDatabaseEntry PrimaryAsset { get; init; }
	public required IReadOnlyList<AssetDatabaseEntry> SubAssets { get; init; }
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
		foreach (var group in assets.GroupBy(asset => asset.SourceId))
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

	private static int GetAssetTypeSortOrder(AssetDatabaseEntry asset)
	{
		return asset.Type switch
		{
			AssetType.Scene => 0,
			AssetType.Model3D => 1,
			AssetType.Mesh => 2,
			AssetType.Material => 3,
			AssetType.Texture2D => 4,
			AssetType.DataAsset => 5,
			_ => 10
		};
	}
}
