using System.Numerics;
using System.Text.Json.Serialization;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;

namespace WolfEngine.Animation;

/// <summary>
/// Drives a skeleton. For now it plays a single clip; the node-based animator graph will replace
/// the pose source behind <see cref="IPoseSource"/> without changing anything that reads from here.
/// </summary>
public struct Animator : IEntityComponent, IJsonOnDeserialized
{
	public AssetRef<Skeleton> SkeletonAsset;
	public AssetRef<AnimationClip> ClipAsset;

	public float Speed;
	public bool Loop;
	public bool Playing;

	/// <summary>Playback position in seconds. Writable so the editor can scrub.</summary>
	public float Time;

	[JsonIgnore] internal Skeleton? Skeleton;
	[JsonIgnore] internal AnimationClip? Clip;
	[JsonIgnore] internal IPoseSource? PoseSource;
	[JsonIgnore] internal Pose? Pose;

	/// <summary>Bone matrices for the current pose, uploaded to the skinning pass each frame.</summary>
	[JsonIgnore] internal Matrix4x4[]? SkinningMatrices;

	/// <summary>Bone matrices for the previous rendered pose, used for motion vectors.</summary>
	[JsonIgnore] internal Matrix4x4[]? PreviousSkinningMatrices;

	/// <summary>Whether the previous-pose matrices have been initialized.</summary>
	[JsonIgnore] internal bool HasPreviousPose;

	/// <summary>Bumped whenever the pose changes, so the renderer can skip unchanged characters.</summary>
	[JsonIgnore] internal uint PoseGeneration;

	public static Animator Create(AssetRef<Skeleton> skeleton, AssetRef<AnimationClip> clip) =>
		new()
		{
			SkeletonAsset = skeleton,
			ClipAsset = clip,
			Speed = 1.0f,
			Loop = true,
			Playing = true,
			Time = 0.0f
		};

	/// <summary>
	/// Resolves assets and (re)builds the pose source when the bound clip or skeleton changes.
	/// Returns false when the animator cannot produce a pose.
	/// </summary>
	internal bool TryPrepare()
	{
		Skeleton ??= SkeletonAsset.IsValid ? SkeletonAsset.Asset : null;
		Clip ??= ClipAsset.IsValid ? ClipAsset.Asset : null;

		if (Skeleton is null || Clip is null)
		{
			return false;
		}

		if (PoseSource is SingleClipPoseSource existing &&
		    ReferenceEquals(existing.Clip, Clip) &&
		    ReferenceEquals(existing.Skeleton, Skeleton))
		{
			return true;
		}

		var source = new SingleClipPoseSource(Clip, Skeleton) { Time = Time };
		PoseSource = source;
		Pose = Clip.CreatePose(Skeleton);
		SkinningMatrices = new Matrix4x4[Skeleton.BoneCount];
		PreviousSkinningMatrices = new Matrix4x4[Skeleton.BoneCount];
		HasPreviousPose = false;
		return true;
	}

	public void OnDeserialized()
	{
		Skeleton = SkeletonAsset.IsValid ? SkeletonAsset.Asset : null;
		Clip = ClipAsset.IsValid ? ClipAsset.Asset : null;
		PoseSource = null;
		Pose = null;
		SkinningMatrices = null;
		PreviousSkinningMatrices = null;
		HasPreviousPose = false;
	}
}

/// <summary>
/// Marks a bone that should also exist as an entity, so gameplay can parent things to it — a weapon
/// in a hand, a camera on a head. Opt-in because the whole point of keeping the pose in a flat array
/// is that a character does not pay ECS transform costs for bones nobody attaches to.
/// </summary>
public struct ExposedBone : IEntityComponent
{
	/// <summary>Entity carrying the <see cref="Animator"/> whose skeleton this bone belongs to.</summary>
	public Entity AnimatorEntity;

	/// <summary>Resolved from <see cref="BoneName"/> on first use.</summary>
	public int BoneIndex;

	public string BoneName;

	public ExposedBone(Entity animatorEntity, string boneName)
	{
		AnimatorEntity = animatorEntity;
		BoneName = boneName ?? string.Empty;
		BoneIndex = -1;
	}
}
