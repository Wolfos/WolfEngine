using System.Numerics;
using Silk.NET.Assimp;
using WolfEngine.Animation;

namespace WolfEngine.Importing;

/// <summary>
/// The result of lifting a bone hierarchy out of an Assimp node graph.
/// </summary>
internal sealed class SkeletonBuildResult
{
	internal SkeletonBuildResult(
		ImportedSkeleton skeleton,
		Dictionary<string, int> boneIndicesByName,
		HashSet<string> skeletonNodeNames)
	{
		Skeleton = skeleton;
		BoneIndicesByName = boneIndicesByName;
		SkeletonNodeNames = skeletonNodeNames;
	}

	internal ImportedSkeleton Skeleton { get; }

	/// <summary>Bone name to index within <see cref="Skeleton"/>.</summary>
	internal Dictionary<string, int> BoneIndicesByName { get; }

	/// <summary>Names of every node folded into the skeleton, used to keep them out of the entity hierarchy.</summary>
	internal HashSet<string> SkeletonNodeNames { get; }
}

/// <summary>
/// Builds a single <see cref="ImportedSkeleton"/> from every skin in the scene.
/// </summary>
/// <remarks>
/// The skeleton spans the whole chain from the scene root down to each bone, not just the bones
/// that vertices are actually weighted to. That is what keeps the maths simple: accumulating local
/// transforms down the skeleton then reproduces Assimp's global node transform exactly, so a bone's
/// offset matrix composes with it directly, and any unit-conversion scale sitting on an exporter's
/// armature node (Mixamo's centimetre scale, for one) is carried along for free instead of having
/// to be detected and re-applied. The handful of extra pass-through bones cost one matrix multiply
/// each and nothing else.
/// </remarks>
internal static class SkeletonBuilder
{
	internal static unsafe SkeletonBuildResult? Build(Scene* scene)
	{
		if (scene is null || scene->MRootNode is null)
		{
			return null;
		}

		var offsetMatricesByBoneName = CollectBoneOffsetMatrices(scene);
		if (offsetMatricesByBoneName.Count == 0)
		{
			return null;
		}

		var nodesByName = new Dictionary<string, nint>(StringComparer.Ordinal);
		var parentsByName = new Dictionary<string, string?>(StringComparer.Ordinal);
		MapNodes(scene->MRootNode, parentName: null, nodesByName, parentsByName);

		// Walk up from every weighted bone to the scene root, so the chain that connects them is
		// complete even when an exporter puts unweighted helper nodes in between.
		var skeletonNodeNames = new HashSet<string>(StringComparer.Ordinal);
		foreach (var boneName in offsetMatricesByBoneName.Keys)
		{
			var current = boneName;
			while (current is not null && skeletonNodeNames.Add(current))
			{
				parentsByName.TryGetValue(current, out current);
			}
		}

		if (skeletonNodeNames.Count == 0)
		{
			return null;
		}

		var boneNames = new List<string>(skeletonNodeNames.Count);
		var parentIndices = new List<int>(skeletonNodeNames.Count);
		var bindPoseLocal = new List<BoneTransform>(skeletonNodeNames.Count);
		var boneIndicesByName = new Dictionary<string, int>(skeletonNodeNames.Count, StringComparer.Ordinal);

		// Depth-first from the root guarantees a parent is emitted before its children, which the
		// Skeleton constructor enforces and pose evaluation relies on.
		AppendBone(scene->MRootNode, parentIndex: -1, skeletonNodeNames, boneNames, parentIndices, bindPoseLocal, boneIndicesByName);

		if (boneNames.Count == 0)
		{
			return null;
		}

		var inverseBindMatrices = BuildInverseBindMatrices(
			boneNames,
			parentIndices,
			bindPoseLocal,
			offsetMatricesByBoneName);

		var skeletonName = string.IsNullOrWhiteSpace(scene->MRootNode->MName.AsString)
			? "Skeleton"
			: scene->MRootNode->MName.AsString;

		var skeleton = new ImportedSkeleton(
			skeletonName,
			boneNames.ToArray(),
			parentIndices.ToArray(),
			bindPoseLocal.ToArray(),
			inverseBindMatrices);

		return new SkeletonBuildResult(skeleton, boneIndicesByName, skeletonNodeNames);
	}

	private static unsafe Dictionary<string, Matrix4x4> CollectBoneOffsetMatrices(Scene* scene)
	{
		var offsetMatrices = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
		for (var meshIndex = 0; meshIndex < scene->MNumMeshes; meshIndex++)
		{
			var mesh = scene->MMeshes[meshIndex];
			if (mesh is null || mesh->MNumBones == 0)
			{
				continue;
			}

			for (var boneIndex = 0; boneIndex < mesh->MNumBones; boneIndex++)
			{
				var bone = mesh->MBones[boneIndex];
				if (bone is null)
				{
					continue;
				}

				var boneName = bone->MName.AsString;
				if (string.IsNullOrEmpty(boneName))
				{
					continue;
				}

				// Meshes sharing a skeleton repeat the same bone with the same offset matrix.
				offsetMatrices.TryAdd(boneName, ThreeDFileImporter.ConvertTransform(bone->MOffsetMatrix));
			}
		}

		return offsetMatrices;
	}

	private static unsafe void MapNodes(
		Node* node,
		string? parentName,
		Dictionary<string, nint> nodesByName,
		Dictionary<string, string?> parentsByName)
	{
		if (node is null)
		{
			return;
		}

		var name = node->MName.AsString;
		if (string.IsNullOrEmpty(name) == false)
		{
			// Duplicate node names are malformed for skinning purposes; first occurrence wins so the
			// mapping stays deterministic rather than depending on traversal order.
			if (nodesByName.TryAdd(name, (nint)node))
			{
				parentsByName[name] = parentName;
			}

			parentName = name;
		}

		for (var i = 0; i < node->MNumChildren; i++)
		{
			MapNodes(node->MChildren[i], parentName, nodesByName, parentsByName);
		}
	}

	private static unsafe void AppendBone(
		Node* node,
		int parentIndex,
		HashSet<string> skeletonNodeNames,
		List<string> boneNames,
		List<int> parentIndices,
		List<BoneTransform> bindPoseLocal,
		Dictionary<string, int> boneIndicesByName)
	{
		if (node is null)
		{
			return;
		}

		var name = node->MName.AsString;
		var childParentIndex = parentIndex;

		if (string.IsNullOrEmpty(name) == false && skeletonNodeNames.Contains(name) && boneIndicesByName.ContainsKey(name) == false)
		{
			var index = boneNames.Count;
			boneNames.Add(name);
			parentIndices.Add(parentIndex);
			bindPoseLocal.Add(BoneTransform.FromMatrix(ThreeDFileImporter.ConvertTransform(node->MTransformation)));
			boneIndicesByName[name] = index;
			childParentIndex = index;
		}

		for (var i = 0; i < node->MNumChildren; i++)
		{
			AppendBone(
				node->MChildren[i],
				childParentIndex,
				skeletonNodeNames,
				boneNames,
				parentIndices,
				bindPoseLocal,
				boneIndicesByName);
		}
	}

	private static Matrix4x4[] BuildInverseBindMatrices(
		List<string> boneNames,
		List<int> parentIndices,
		List<BoneTransform> bindPoseLocal,
		Dictionary<string, Matrix4x4> offsetMatricesByBoneName)
	{
		var inverseBind = new Matrix4x4[boneNames.Count];
		var modelBind = new Matrix4x4[boneNames.Count];

		for (var i = 0; i < boneNames.Count; i++)
		{
			var local = bindPoseLocal[i].ToMatrix();
			var parentIndex = parentIndices[i];
			modelBind[i] = parentIndex >= 0 ? local * modelBind[parentIndex] : local;

			if (offsetMatricesByBoneName.TryGetValue(boneNames[i], out var offsetMatrix))
			{
				inverseBind[i] = offsetMatrix;
				continue;
			}

			// A pass-through node with no vertices weighted to it. Inverting its bind pose is the
			// value that would make it a no-op if something ever does bind to it.
			inverseBind[i] = Matrix4x4.Invert(modelBind[i], out var inverted)
				? inverted
				: Matrix4x4.Identity;
		}

		return inverseBind;
	}
}
