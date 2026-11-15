using System.Numerics;
using ImGuiNET;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.UI;

public sealed record AssetBrowserRect(float X, float Y, float Width, float Height);
public sealed record AssetBrowserItemState(
	string Name,
	string Path,
	string Kind,
	Guid? AssetId,
	Guid? SourceId,
	string? AssetType,
	int SubAssetCount,
	AssetBrowserRect Bounds);
public sealed record AssetBrowserSourceState(Guid SourceId, Guid AssetId, string Name, string Type, string Path, int SubAssetCount);
public sealed record AssetBrowserContextState(string Kind, string Path, Guid? AssetId);
public sealed record AssetBrowserRenameState(string Kind, string Path, string Name);
public sealed record AssetBrowserState(
	string FolderPath,
	string Search,
	Guid? SelectedAssetId,
	Guid? ExpandedSourceId,
	string SubAssetSearch,
	string? SubAssetType,
	int MatchingSubAssets,
	AssetBrowserContextState? ContextMenu,
	AssetBrowserRenameState? RenameDialog,
	IReadOnlyList<AssetBrowserSourceState> Sources,
	IReadOnlyList<AssetBrowserItemState> Items,
	IReadOnlyDictionary<string, AssetBrowserRect> Controls);
public sealed partial class AssetsWindow
{
	private readonly List<AssetBrowserItemState> _browserItems = new();
	private readonly Dictionary<string, AssetBrowserRect> _browserControls = new();
	private AssetsWindowBrowserModel? _lastBrowserModel;
	private AssetsWindowFolderContents? _lastContents;

	public AssetBrowserState GetBrowserState()
	{
		return new AssetBrowserState(
			_selection.SelectedFolderPath,
			_assetSearchText,
			_assetSelectionService.SelectedAssetId,
			_selection.ExpandedSourceId,
			_subAssetSearch,
			_subAssetType?.ToString(),
			_filteredSubAssets.Count,
			_browserContextMenuVisible && _browserContextTarget is { } target
				? new AssetBrowserContextState(target.Kind.ToString(), target.RelativeSourcePath ?? target.FolderPath, target.AssetId)
				: null,
			_pendingRenameTarget is { } rename
				? new AssetBrowserRenameState(rename.Kind.ToString(), rename.RelativePath, _renameName)
				: null,
			_lastContents?.Sources.Select(source => new AssetBrowserSourceState(
			source.SourceId,
			source.PrimaryAsset.Id,
			source.DisplayName,
			source.PrimaryAsset.Type.ToString(),
			source.RelativeSourcePath,
			source.SubAssets.Count)).ToArray() ?? [],
			_browserItems.ToArray(),
			new Dictionary<string, AssetBrowserRect>(_browserControls));
	}

	public void ConfigureBrowser(
		string folderPath,
		string search = "",
		Guid? expandedSourceId = null,
		string subAssetSearch = "")
	{
		var model = _lastBrowserModel ?? _browserModelCache.GetOrBuild(
			_projectService.CurrentAssetCatalog,
			_projectService.AssetsPath!,
			_projectService.AssetDatabaseRevision);
		if (model.FoldersByPath.ContainsKey(folderPath) == false)
		{
			throw new ArgumentException($"Browser folder '{folderPath}' was not found.", nameof(folderPath));
		}

		SelectFolder(folderPath);
		_assetSearchText = search;
		if (expandedSourceId is { } id)
		{
			if (model.SourcesBySourceId.TryGetValue(id, out var source) == false || source.SubAssets.Count == 0)
			{
				throw new ArgumentException("The source has no browsable sub-assets.", nameof(expandedSourceId));
			}

			_selection.ExpandedSourceId = id;
			_subAssetViewSource = id;
			_subAssetType = null;
			_subAssetSearch = subAssetSearch;
		}
	}

	private static AssetBrowserRect BrowserRect(Vector2 min, Vector2 max) => new(min.X, min.Y, max.X - min.X, max.Y - min.Y);
	private void RecordBrowserControl(string name)
	{
		_browserControls[name] = BrowserRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
	}

	private void RecordBrowserItem(string path, Guid? assetId, Vector2 min, Vector2 max, bool subAsset = false)
	{
		AssetDatabaseEntry? asset = null;
		if (assetId.HasValue)
		{
			_projectService.TryGetAsset(assetId.Value, out asset);
		}

		var count = asset is not null && _lastBrowserModel?.SourcesBySourceId.TryGetValue(asset.SourceId, out var source) == true ? source.SubAssets.Count : 0;
		_browserItems.Add(new AssetBrowserItemState(
			asset?.Name ?? Path.GetFileName(path),
			path,
			subAsset ? "SubAsset" : asset is null ? "Folder" : "Source",
			assetId,
			asset?.SourceId,
			asset?.Type.ToString(),
			count,
			BrowserRect(min, max)));
	}
}
