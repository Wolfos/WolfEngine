using System.Numerics;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Animation;

/// <summary>
/// A bone hierarchy shared by every mesh skinned to it. Bones are not entities: a character can
/// carry hundreds, and pushing those through the ECS transform hierarchy every frame does not
/// survive open-world crowd counts. Entities are created only for bones explicitly exposed as
/// attachment sockets.
/// </summary>
[RuntimeAsset(AssetType.Skeleton, typeof(ImportedSkeletonAssetFile), typeof(ISkeletonRuntimeAssetResolver))]
public sealed class Skeleton
{
	private readonly Dictionary<string, int> _boneIndicesByName;

	public Skeleton(
		string name,
		string[] boneNames,
		int[] parentIndices,
		BoneTransform[] bindPoseLocal,
		Matrix4x4[] inverseBindMatrices)
	{
		Name = name ?? string.Empty;
		BoneNames = boneNames ?? throw new ArgumentNullException(nameof(boneNames));
		ParentIndices = parentIndices ?? throw new ArgumentNullException(nameof(parentIndices));
		BindPoseLocal = bindPoseLocal ?? throw new ArgumentNullException(nameof(bindPoseLocal));
		InverseBindMatrices = inverseBindMatrices ?? throw new ArgumentNullException(nameof(inverseBindMatrices));

		if (ParentIndices.Length != BoneNames.Length ||
		    BindPoseLocal.Length != BoneNames.Length ||
		    InverseBindMatrices.Length != BoneNames.Length)
		{
			throw new ArgumentException(
				$"Skeleton '{Name}' has mismatched bone arrays: {BoneNames.Length} names, " +
				$"{ParentIndices.Length} parents, {BindPoseLocal.Length} bind transforms, " +
				$"{InverseBindMatrices.Length} inverse bind matrices.");
		}

		// Pose evaluation walks bones in order and reads its parent's already-computed model matrix,
		// so a parent appearing after its child would silently produce a frame-late transform.
		for (var i = 0; i < ParentIndices.Length; i++)
		{
			if (ParentIndices[i] >= i)
			{
				throw new ArgumentException(
					$"Skeleton '{Name}' bone {i} ('{BoneNames[i]}') has parent index {ParentIndices[i]}; " +
					"parents must precede their children.",
					nameof(parentIndices));
			}
		}

		_boneIndicesByName = new Dictionary<string, int>(BoneNames.Length, StringComparer.Ordinal);
		for (var i = 0; i < BoneNames.Length; i++)
		{
			// Duplicate bone names are legal in some exporters; first occurrence wins, which keeps
			// clip binding deterministic.
			_boneIndicesByName.TryAdd(BoneNames[i], i);
		}
	}

	public string Name { get; }
	public string[] BoneNames { get; }

	/// <summary>Parent bone index per bone, or -1 for a root. Always less than the bone's own index.</summary>
	public int[] ParentIndices { get; }

	public BoneTransform[] BindPoseLocal { get; }

	/// <summary>Mesh space to bone space, from Assimp's bone offset matrices.</summary>
	public Matrix4x4[] InverseBindMatrices { get; }

	public int BoneCount => BoneNames.Length;

	public bool TryGetBoneIndex(string boneName, out int index)
	{
		if (string.IsNullOrEmpty(boneName))
		{
			index = -1;
			return false;
		}

		return _boneIndicesByName.TryGetValue(boneName, out index);
	}

	public Pose CreatePose(int transformCount = 0, int valueCount = 0)
	{
		var pose = new Pose(BoneCount, transformCount, valueCount);
		pose.SetToBindPose(this);
		return pose;
	}
}
