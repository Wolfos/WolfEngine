using System;

namespace WolfEngine.Animation;

/// <summary>
/// Plays one clip at one speed. This is the whole of the animation "graph" for now; blending,
/// state machines and layers all arrive as other <see cref="IPoseSource"/> implementations rather
/// than as changes here.
/// </summary>
public sealed class SingleClipPoseSource : IPoseSource
{
	private readonly BoneRemap _remap;
	private readonly int[] _positionCursors;
	private readonly int[] _rotationCursors;
	private readonly int[] _scaleCursors;
	private readonly int[] _valueCursors;

	public SingleClipPoseSource(AnimationClip clip, Skeleton skeleton)
	{
		Clip = clip ?? throw new ArgumentNullException(nameof(clip));
		Skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
		_remap = BoneRemap.Get(clip, skeleton);

		var trackCount = clip.TransformTracks.Length;
		_positionCursors = new int[trackCount];
		_rotationCursors = new int[trackCount];
		_scaleCursors = new int[trackCount];
		_valueCursors = new int[clip.PropertyTracks.Length];
	}

	public AnimationClip Clip { get; }
	public Skeleton Skeleton { get; }

	public float Time { get; set; }
	public float Speed { get; set; } = 1.0f;
	public bool Playing { get; set; } = true;

	/// <summary>How many of the clip's bone tracks actually resolved against this skeleton.</summary>
	public int MatchedBoneTrackCount => _remap.MatchedBoneTrackCount;

	public int UnmatchedBoneTrackCount => _remap.UnmatchedBoneTrackCount;

	public void Evaluate(float deltaTime, Pose destination)
	{
		ArgumentNullException.ThrowIfNull(destination);

		if (Playing && deltaTime != 0.0f)
		{
			Time = Clip.NormalizeTime(Time + (deltaTime * Speed));
		}
		else
		{
			Time = Clip.NormalizeTime(Time);
		}

		var time = Time;
		var tracks = Clip.TransformTracks;
		var bindPose = Skeleton.BindPoseLocal;

		for (var i = 0; i < tracks.Length; i++)
		{
			var track = tracks[i];
			var boneIndex = _remap.TrackToBone[i];
			if (boneIndex >= 0)
			{
				// Falling back to the bind pose channel by channel matters: exporters routinely emit
				// a rotation curve with no translation curve, and the rest value is the right answer
				// for the missing channel.
				var bind = bindPose[boneIndex];
				destination.Bones[boneIndex] = new BoneTransform(
					track.Position.Evaluate(time, ref _positionCursors[i], bind.Position),
					track.Rotation.Evaluate(time, ref _rotationCursors[i], bind.Rotation),
					track.Scale.Evaluate(time, ref _scaleCursors[i], bind.Scale));
				continue;
			}

			var transformSlot = _remap.TrackToTransformSlot[i];
			if (transformSlot < 0 || transformSlot >= destination.Transforms.Length)
			{
				// Either a bone track this skeleton does not have, or a non-bone track the
				// destination pose was not sized for. Both are survivable; skip it.
				continue;
			}

			var previous = destination.Transforms[transformSlot];
			destination.Transforms[transformSlot] = new BoneTransform(
				track.Position.Evaluate(time, ref _positionCursors[i], previous.Position),
				track.Rotation.Evaluate(time, ref _rotationCursors[i], previous.Rotation),
				track.Scale.Evaluate(time, ref _scaleCursors[i], previous.Scale));
		}

		var propertyTracks = Clip.PropertyTracks;
		var valueCount = Math.Min(propertyTracks.Length, destination.Values.Length);
		for (var i = 0; i < valueCount; i++)
		{
			destination.Values[i] = propertyTracks[i].Curve.Evaluate(
				time,
				ref _valueCursors[i],
				destination.Values[i]);
		}
	}
}
