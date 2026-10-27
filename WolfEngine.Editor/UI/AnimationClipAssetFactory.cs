using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

/// <summary>Writes new authored clip files into the project and imports them.</summary>
internal static class AnimationClipAssetFactory
{
	public const string DefaultName = "New Animation Clip";
	public const string DefaultFolder = "Assets";

	/// <summary>A project-relative path for a new clip in the folder that no file uses yet.</summary>
	public static string GetUniquePath(IEditorProjectService project, string folder, string name)
	{
		var baseName = SanitizeFileName(string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim());
		var directory = string.IsNullOrWhiteSpace(folder) ? DefaultFolder : folder.Replace('\\', '/').TrimEnd('/');
		var path = $"{directory}/{baseName}{AuthoredAnimationClipFile.Extension}";
		var suffix = 1;
		while (File.Exists(project.GetAbsolutePath(path)))
		{
			path = $"{directory}/{baseName} {suffix}{AuthoredAnimationClipFile.Extension}";
			suffix++;
		}

		return path;
	}

	/// <summary>Writes the clip, imports it and returns its asset entry.</summary>
	public static AssetDatabaseEntry Create(
		IEditorProjectService project,
		IProjectAssetPipelineService pipeline,
		string relativePath,
		AuthoredAnimationClipFile clip)
	{
		ArgumentNullException.ThrowIfNull(clip);
		var projectRoot = project.ProjectRootPath
			?? throw new InvalidOperationException("Open a project to create animation clips.");

		clip.Validate();
		AuthoredAnimationClipJson.Write(project.GetAbsolutePath(relativePath), clip);
		project.RefreshAssetSource(relativePath);

		if (pipeline.TryGetPrimaryNodeIdForRelativeSourcePath(projectRoot, relativePath, out var assetId) == false ||
		    project.TryGetAsset(assetId, out var asset) == false)
		{
			throw new InvalidOperationException($"'{relativePath}' was written but not imported.");
		}

		return asset;
	}

	/// <summary>The folder a source file lives in, as a project-relative path.</summary>
	public static string GetFolder(string relativeSourcePath)
	{
		var normalized = relativeSourcePath.Replace('\\', '/');
		var slash = normalized.LastIndexOf('/');
		return slash > 0 ? normalized[..slash] : DefaultFolder;
	}

	/// <summary>
	/// The skeleton a selected asset implies: the skeleton itself, or the skeleton imported from the
	/// same file as a selected model. Empty when the selection says nothing about a skeleton.
	/// </summary>
	public static Guid FindSkeletonForAsset(AssetDatabaseEntry? asset, IReadOnlyList<AssetDatabaseEntry> assets)
	{
		if (asset is null)
		{
			return Guid.Empty;
		}

		if (asset.Type == AssetType.Skeleton)
		{
			return asset.Id;
		}

		if (asset.Type != AssetType.Model3D)
		{
			return Guid.Empty;
		}

		foreach (var candidate in assets)
		{
			if (candidate.Type == AssetType.Skeleton && candidate.SourceId == asset.SourceId)
			{
				return candidate.Id;
			}
		}

		return Guid.Empty;
	}

	private static string SanitizeFileName(string name)
	{
		var invalid = Path.GetInvalidFileNameChars();
		var characters = name.ToCharArray();
		for (var i = 0; i < characters.Length; i++)
		{
			if (Array.IndexOf(invalid, characters[i]) >= 0 || characters[i] == '/' || characters[i] == '\\')
			{
				characters[i] = '_';
			}
		}

		return new string(characters);
	}
}
