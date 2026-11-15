using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

internal sealed class AssetsWindowSelectionState
{
	public string SelectedFolderPath { get; private set; } = AssetPipelinePaths.AssetsFolderName;
	public string? FolderTreeRevealPath { get; private set; }
	public Guid? ExpandedSourceId { get; set; }
	private Guid? _observedAssetId;

	public void ObserveBrowserSelection(Guid assetId)
	{
		_observedAssetId = assetId;
	}

	public void SetSelectedFolderPath(string relativeFolderPath)
	{
		var normalizedFolderPath = AssetsWindowBrowserPaths.Normalize(relativeFolderPath);
		if (string.Equals(SelectedFolderPath, normalizedFolderPath, StringComparison.OrdinalIgnoreCase) == false)
		{
			FolderTreeRevealPath = normalizedFolderPath;
		}

		SelectedFolderPath = normalizedFolderPath;
	}

	public void ClearFolderRevealPath()
	{
		FolderTreeRevealPath = null;
	}

	public void RevealFolderPath(string relativeFolderPath)
	{
		FolderTreeRevealPath = AssetsWindowBrowserPaths.Normalize(relativeFolderPath);
	}

	public void Prune(
		AssetsWindowBrowserModel browserModel,
		IEditorProjectService projectService,
		IAssetSelectionService assetSelectionService)
	{
		SetSelectedFolderPath(
			AssetsWindowBrowserModelBuilder.NormalizeSelectedFolderPath(browserModel, SelectedFolderPath));
		if (ExpandedSourceId.HasValue && browserModel.SourcesBySourceId.ContainsKey(ExpandedSourceId.Value) == false)
		{
			ExpandedSourceId = null;
		}

		if (assetSelectionService.SelectedAssetId is { } selectedAssetId &&
		    projectService.TryGetAsset(selectedAssetId, out var selectedAsset))
		{
			if (_observedAssetId != selectedAssetId)
			{
				browserModel.SourcesBySourceId.TryGetValue(selectedAsset.SourceId, out var source);
				SetSelectedFolderPath(source?.FolderPath ?? ProjectPathUtility.GetFolderPath(selectedAsset.RelativeSourcePath));
				if (source is not null && source.PrimaryAsset.Id != selectedAssetId)
				{
					ExpandedSourceId = source.SourceId;
				}
				else if (ExpandedSourceId.HasValue)
				{
					ExpandedSourceId = source?.SubAssets.Count > 0 ? source.SourceId : null;
				}
			}

			_observedAssetId = selectedAssetId;
		}
		else if (assetSelectionService.SelectedAssetId.HasValue)
		{
			assetSelectionService.Clear();
			_observedAssetId = null;
		}
		else
		{
			_observedAssetId = null;
		}
	}

	public void ValidateAfterProjectMutation(
		IEditorProjectService projectService,
		IAssetSelectionService assetSelectionService)
	{
		if (assetSelectionService.SelectedAssetId is { } selectedAssetId &&
		    projectService.TryGetAsset(selectedAssetId, out _) == false)
		{
			assetSelectionService.Clear();
		}

		if (projectService.HasOpenProject)
		{
			SetSelectedFolderPath(GetNearestExistingFolderPath(projectService, SelectedFolderPath));
		}

		if (ExpandedSourceId.HasValue &&
		    projectService.CurrentAssetCatalog.Assets.Any(asset => asset.Asset.SourceId == ExpandedSourceId.Value) == false)
		{
			ExpandedSourceId = null;
		}
	}

	public void UpdateSelectedFolderAfterRelocation(string oldFolderPath, string newFolderPath)
	{
		var normalizedOldPath = AssetsWindowBrowserPaths.Normalize(oldFolderPath);
		var normalizedNewPath = AssetsWindowBrowserPaths.Normalize(newFolderPath);
		if (string.Equals(SelectedFolderPath, normalizedOldPath, StringComparison.OrdinalIgnoreCase))
		{
			SetSelectedFolderPath(normalizedNewPath);
			return;
		}

		var oldPrefix = normalizedOldPath + "/";
		if (SelectedFolderPath.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
		{
			SetSelectedFolderPath(ProjectPathUtility.NormalizeRelativePath(
				normalizedNewPath + "/" + SelectedFolderPath[oldPrefix.Length..]));
		}
	}

	public static string GetNearestExistingFolderPath(
		IEditorProjectService projectService,
		string relativeFolderPath)
	{
		var normalizedFolderPath = AssetsWindowBrowserPaths.Normalize(relativeFolderPath);
		// Mounted roots have no project directory to probe; Prune validates them against the browser model.
		if (AssetsWindowBrowserPaths.IsProjectPath(normalizedFolderPath) == false)
		{
			return normalizedFolderPath;
		}

		while (string.Equals(normalizedFolderPath, AssetPipelinePaths.AssetsFolderName,
			       StringComparison.OrdinalIgnoreCase) == false)
		{
			var absoluteFolderPath = projectService.GetAbsolutePath(normalizedFolderPath);
			if (Directory.Exists(absoluteFolderPath))
			{
				return normalizedFolderPath;
			}

			normalizedFolderPath = ProjectPathUtility.GetParentFolderPath(normalizedFolderPath);
		}

		return AssetPipelinePaths.AssetsFolderName;
	}
}
