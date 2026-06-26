using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

internal sealed record BrowserContextTarget(
	BrowserContextKind Kind,
	string FolderPath,
	string? RelativeSourcePath,
	Guid? SourceId,
	Guid? AssetId)
{
	public static BrowserContextTarget ForCurrentFolder(string folderPath) =>
		new(BrowserContextKind.CurrentFolder, folderPath, null, null, null);

	public static BrowserContextTarget ForFolder(string folderPath) =>
		new(BrowserContextKind.Folder, folderPath, null, null, null);

	public static BrowserContextTarget ForSource(string relativeSourcePath, Guid sourceId, Guid assetId) =>
		new(BrowserContextKind.Source, ProjectPathUtility.GetFolderPath(relativeSourcePath), relativeSourcePath,
			sourceId, assetId);

	public static BrowserContextTarget ForSubAsset(string relativeSourcePath, Guid sourceId, Guid assetId) =>
		new(BrowserContextKind.SubAsset, ProjectPathUtility.GetFolderPath(relativeSourcePath), relativeSourcePath,
			sourceId, assetId);
}

internal enum BrowserContextKind
{
	CurrentFolder,
	Folder,
	Source,
	SubAsset
}

internal sealed record AssetBrowserDragTarget(DragTargetKind Kind, string RelativePath)
{
	public static AssetBrowserDragTarget ForSource(string relativeSourcePath) =>
		new(DragTargetKind.Source, relativeSourcePath);

	public static AssetBrowserDragTarget ForFolder(string relativeFolderPath) =>
		new(DragTargetKind.Folder, relativeFolderPath);
}

internal enum DragTargetKind
{
	Source,
	Folder
}

internal sealed record PendingDeleteTarget(
	DeleteTargetKind Kind,
	string RelativePath,
	string DisplayName,
	string ConfirmationText)
{
	public static PendingDeleteTarget ForSource(string relativeSourcePath)
	{
		var displayName = Path.GetFileName(relativeSourcePath);
		return new PendingDeleteTarget(
			DeleteTargetKind.Source,
			relativeSourcePath,
			displayName,
			$"Delete '{displayName}' and all derived assets? This permanently removes the source file and its .meta file.");
	}

	public static PendingDeleteTarget ForFolder(string relativeFolderPath)
	{
		var displayName = Path.GetFileName(relativeFolderPath);
		return new PendingDeleteTarget(
			DeleteTargetKind.Folder,
			relativeFolderPath,
			displayName,
			$"Delete folder '{displayName}' and everything inside it? This permanently removes all files and derived assets under that folder.");
	}
}

internal enum DeleteTargetKind
{
	Source,
	Folder
}

internal sealed record PendingRenameTarget(
	RenameTargetKind Kind,
	string RelativePath,
	string EditableName,
	string Suffix,
	string Title,
	bool FocusInput = true)
{
	public static PendingRenameTarget ForSource(string relativeSourcePath)
	{
		var fileName = Path.GetFileName(relativeSourcePath);
		var suffix = GetAssetSourceSuffix(fileName);
		var editableName = suffix.Length == 0 ? fileName : fileName[..^suffix.Length];
		return new PendingRenameTarget(
			RenameTargetKind.Source,
			relativeSourcePath,
			editableName,
			suffix,
			"Rename Asset");
	}

	public static PendingRenameTarget ForFolder(string relativeFolderPath)
	{
		var folderName = Path.GetFileName(relativeFolderPath);
		return new PendingRenameTarget(
			RenameTargetKind.Folder,
			relativeFolderPath,
			folderName,
			string.Empty,
			"Rename Folder");
	}

	private static string GetAssetSourceSuffix(string fileName)
	{
		string[] compoundSuffixes =
		[
			MaterialAsset.FileExtension,
			DataAssetFile.FileExtension,
			EditorSceneAssetFile.FileExtension,
			PrefabAssetFile.FileExtension
		];

		for (var i = 0; i < compoundSuffixes.Length; i++)
		{
			if (fileName.EndsWith(compoundSuffixes[i], StringComparison.OrdinalIgnoreCase))
			{
				return fileName[^compoundSuffixes[i].Length..];
			}
		}

		return Path.GetExtension(fileName);
	}
}

internal enum RenameTargetKind
{
	Source,
	Folder
}
