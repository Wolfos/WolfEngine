using System;
using System.IO;
using System.Linq;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

internal static class ProjectPathUtility
{
	private static readonly StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;

	public static string NormalizeRelativePath(string relativePath)
	{
		return relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
	}

	public static string NormalizeAssetsFolderPath(string? relativeFolderPath)
	{
		var normalized = string.IsNullOrWhiteSpace(relativeFolderPath)
			? AssetPipelinePaths.AssetsFolderName
			: NormalizeRelativePath(relativeFolderPath).TrimEnd('/');
		if (string.IsNullOrWhiteSpace(normalized))
		{
			normalized = AssetPipelinePaths.AssetsFolderName;
		}

		if (IsAssetsPathOrDescendant(normalized) == false)
		{
			throw new InvalidOperationException($"Path '{relativeFolderPath}' must be inside the Assets folder.");
		}

		return normalized;
	}

	public static bool IsAssetsPathOrDescendant(string relativePath)
	{
		var normalized = NormalizeRelativePath(relativePath).TrimEnd('/');
		var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Any(segment => string.Equals(segment, ".", StringComparison.Ordinal) ||
		                            string.Equals(segment, "..", StringComparison.Ordinal)))
		{
			return false;
		}

		return string.Equals(normalized, AssetPipelinePaths.AssetsFolderName, PathComparison)
		       || normalized.StartsWith(AssetPipelinePaths.AssetsFolderName + "/", PathComparison);
	}

	public static bool IsSameOrDescendant(string candidatePath, string parentPath)
	{
		var normalizedCandidate = NormalizeRelativePath(candidatePath).TrimEnd('/');
		var normalizedParent = NormalizeRelativePath(parentPath).TrimEnd('/');
		return string.Equals(normalizedCandidate, normalizedParent, PathComparison)
		       || normalizedCandidate.StartsWith(normalizedParent + "/", PathComparison);
	}

	public static string GetFolderPath(string relativePath)
	{
		var normalized = NormalizeRelativePath(relativePath);
		var directoryPath = Path.GetDirectoryName(normalized);
		return string.IsNullOrWhiteSpace(directoryPath)
			? AssetPipelinePaths.AssetsFolderName
			: NormalizeRelativePath(directoryPath);
	}

	public static string GetParentFolderPath(string relativeFolderPath)
	{
		var normalized = NormalizeAssetsFolderPath(relativeFolderPath);
		if (string.Equals(normalized, AssetPipelinePaths.AssetsFolderName, PathComparison))
		{
			return normalized;
		}

		var parentPath = Path.GetDirectoryName(normalized);
		return string.IsNullOrWhiteSpace(parentPath)
			? AssetPipelinePaths.AssetsFolderName
			: NormalizeRelativePath(parentPath);
	}
}
