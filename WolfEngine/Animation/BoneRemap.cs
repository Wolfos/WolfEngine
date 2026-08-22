using System.Runtime.CompilerServices;

namespace WolfEngine.Animation;

/// <summary>
/// Resolves a clip's tracks against a specific skeleton, once, and caches the result.
/// </summary>
/// <remarks>
/// This is the seam humanoid retargeting plugs into. Today the mapping is a straight name match,
/// so a clip only drives a skeleton that shares its bone names. Retargeting replaces this one
/// lookup with a humanoid-rig mapping plus per-bone basis correction and hip translation scaling;
/// because every other stage addresses bones through the remap rather than through the clip's own
/// ordering, nothing downstream has to change to support it.
/// </remarks>
public sealed class BoneRemap
{
	private static readonly ConditionalWeakTable<AnimationClip, ConditionalWeakTable<Skeleton, BoneRemap>> Cache = new();

	private BoneRemap(int[] trackToBone, int[] trackToTransformSlot, int matchedBoneTrackCount, int unmatchedBoneTrackCount)
	{
		TrackToBone = trackToBone;
		TrackToTransformSlot = trackToTransformSlot;
		MatchedBoneTrackCount = matchedBoneTrackCount;
		UnmatchedBoneTrackCount = unmatchedBoneTrackCount;
	}

	/// <summary>Skeleton bone index per transform track, or -1 when the track is not a matched bone track.</summary>
	public int[] TrackToBone { get; }

	/// <summary>Index into <see cref="Pose.Transforms"/> per transform track, or -1 for bone tracks.</summary>
	public int[] TrackToTransformSlot { get; }

	public int MatchedBoneTrackCount { get; }

	/// <summary>
	/// Bone tracks whose name is absent from the skeleton. Non-zero is not an error — extra fingers
	/// or props are common — but a clip that matches nothing is almost always a rig mismatch.
	/// </summary>
	public int UnmatchedBoneTrackCount { get; }

	public bool HasAnyBoneMatch => MatchedBoneTrackCount > 0;

	public static BoneRemap Get(AnimationClip clip, Skeleton skeleton)
	{
		ArgumentNullException.ThrowIfNull(clip);
		ArgumentNullException.ThrowIfNull(skeleton);

		var perSkeleton = Cache.GetValue(clip, static _ => new ConditionalWeakTable<Skeleton, BoneRemap>());
		if (perSkeleton.TryGetValue(skeleton, out var cached))
		{
			return cached;
		}

		var created = Build(clip, skeleton);
		// Another thread may have built the same remap concurrently; either instance is equivalent.
		return perSkeleton.GetValue(skeleton, _ => created);
	}

	private static BoneRemap Build(AnimationClip clip, Skeleton skeleton)
	{
		var tracks = clip.TransformTracks;
		var trackToBone = new int[tracks.Length];
		var trackToTransformSlot = new int[tracks.Length];
		var matched = 0;
		var unmatched = 0;
		var transformSlot = 0;

		for (var i = 0; i < tracks.Length; i++)
		{
			var track = tracks[i];
			if (track.IsBoneTrack == false)
			{
				trackToBone[i] = -1;
				trackToTransformSlot[i] = transformSlot++;
				continue;
			}

			trackToTransformSlot[i] = -1;
			if (skeleton.TryGetBoneIndex(track.Binding.Path, out var boneIndex))
			{
				trackToBone[i] = boneIndex;
				matched++;
			}
			else
			{
				trackToBone[i] = -1;
				unmatched++;
			}
		}

		return new BoneRemap(trackToBone, trackToTransformSlot, matched, unmatched);
	}
}
