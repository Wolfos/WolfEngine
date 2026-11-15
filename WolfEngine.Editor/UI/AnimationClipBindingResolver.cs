using WolfEngine.Animation;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.UI;

/// <summary>The skeleton a clip is edited against and the model the clip editor previews it on.</summary>
public readonly record struct AnimationClipBinding(Guid SkeletonId, Guid PreviewModelId);

/// <summary>
/// Works out which skeleton and preview model belong to a clip asset. Authored clips name their
/// skeleton; imported clips are bound through their import dependencies, preferring an explicit
/// rig over the skeleton found in the same source file.
/// </summary>
internal static class AnimationClipBindingResolver
{
	public const string RigDependencyKind = "animation-rig";
	public const string SkeletonDependencyKind = "animation-skeleton";

	public static bool TryResolve(
		AssetDatabaseEntry clipAsset,
		AuthoredAnimationClipFile? authored,
		IReadOnlyList<AssetDatabaseEntry> assets,
		IReadOnlyList<AssetDependencyRecord> dependencies,
		out AnimationClipBinding binding,
		out string error)
	{
		ArgumentNullException.ThrowIfNull(clipAsset);
		ArgumentNullException.ThrowIfNull(assets);
		ArgumentNullException.ThrowIfNull(dependencies);

		binding = default;

		var skeletonId = authored is not null
			? authored.Skeleton.NodeId
			: FindImportedSkeletonId(clipAsset, assets, dependencies);
		if (skeletonId == Guid.Empty)
		{
			error = $"'{clipAsset.Name}' has no skeleton to edit against.";
			return false;
		}

		var skeletonAsset = FindAsset(assets, skeletonId);
		if (skeletonAsset is null || skeletonAsset.Type != AssetType.Skeleton)
		{
			error = $"The skeleton of '{clipAsset.Name}' is missing from the project.";
			return false;
		}

		var previewModelId = authored is not null && IsPreviewModel(FindAsset(assets, authored.PreviewModelId))
			? authored.PreviewModelId
			: FindModelForSkeleton(skeletonAsset, assets);
		if (previewModelId == Guid.Empty)
		{
			error = $"No model in the project uses the skeleton of '{clipAsset.Name}', so there is nothing to preview it on.";
			return false;
		}

		binding = new AnimationClipBinding(skeletonId, previewModelId);
		error = string.Empty;
		return true;
	}

	private static Guid FindImportedSkeletonId(
		AssetDatabaseEntry clipAsset,
		IReadOnlyList<AssetDatabaseEntry> assets,
		IReadOnlyList<AssetDependencyRecord> dependencies)
	{
		var skeletonDependency = Guid.Empty;
		for (var i = 0; i < dependencies.Count; i++)
		{
			var dependency = dependencies[i];
			if (dependency.FromNodeId != clipAsset.Id)
			{
				continue;
			}

			if (string.Equals(dependency.Kind, RigDependencyKind, StringComparison.Ordinal))
			{
				return dependency.ToNodeId;
			}

			if (string.Equals(dependency.Kind, SkeletonDependencyKind, StringComparison.Ordinal))
			{
				skeletonDependency = dependency.ToNodeId;
			}
		}

		if (skeletonDependency != Guid.Empty)
		{
			return skeletonDependency;
		}

		for (var i = 0; i < assets.Count; i++)
		{
			if (assets[i].Type == AssetType.Skeleton && assets[i].SourceId == clipAsset.SourceId)
			{
				return assets[i].Id;
			}
		}

		return Guid.Empty;
	}

	/// <summary>The model imported from the same source file as the skeleton is the one that is skinned to it.</summary>
	private static Guid FindModelForSkeleton(AssetDatabaseEntry skeletonAsset, IReadOnlyList<AssetDatabaseEntry> assets)
	{
		for (var i = 0; i < assets.Count; i++)
		{
			if (assets[i].Type == AssetType.Model3D && assets[i].SourceId == skeletonAsset.SourceId)
			{
				return assets[i].Id;
			}
		}

		return Guid.Empty;
	}

	private static bool IsPreviewModel(AssetDatabaseEntry? asset) =>
		asset is not null &&
		(asset.Type == AssetType.Model3D || asset.Type == AssetType.Prefab);

	private static AssetDatabaseEntry? FindAsset(IReadOnlyList<AssetDatabaseEntry> assets, Guid id)
	{
		if (id == Guid.Empty)
		{
			return null;
		}

		for (var i = 0; i < assets.Count; i++)
		{
			if (assets[i].Id == id)
			{
				return assets[i];
			}
		}

		return null;
	}
}
