using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

internal sealed class AssetsWindowSelectionState
{
	public string SelectedFolderPath { get; private set; } = AssetPipelinePaths.AssetsFolderName;
	public string? FolderTreeRevealPath { get; private set; }
	public Guid? ExpandedSourceId { get; set; }

	public void SetSelectedFolderPath(string relativeFolderPath)
	{
		var normalizedFolderPath = ProjectPathUtility.NormalizeAssetsFolderPath(relativeFolderPath);
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
		FolderTreeRevealPath = ProjectPathUtility.NormalizeAssetsFolderPath(relativeFolderPath);
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
			SetSelectedFolderPath(ProjectPathUtility.GetFolderPath(selectedAsset.RelativeSourcePath));
		}
		else if (assetSelectionService.SelectedAssetId.HasValue)
		{
			assetSelectionService.Clear();
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
		    projectService.CurrentAssetDatabase.Assets.Any(asset => asset.SourceId == ExpandedSourceId.Value) == false)
		{
			ExpandedSourceId = null;
		}
	}

	public void UpdateSelectedFolderAfterRelocation(string oldFolderPath, string newFolderPath)
	{
		var normalizedOldPath = ProjectPathUtility.NormalizeAssetsFolderPath(oldFolderPath);
		var normalizedNewPath = ProjectPathUtility.NormalizeAssetsFolderPath(newFolderPath);
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
		var normalizedFolderPath = ProjectPathUtility.NormalizeAssetsFolderPath(relativeFolderPath);
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
