namespace WolfEngine.Animation;

/// <summary>
/// Samples one clip straight into a pose, without an animation graph. Tools use this to show a clip
/// exactly as authored; untracked bones and empty channels keep the bind pose, matching what a clip
/// node produces at runtime.
/// </summary>
public static class AnimationClipPoseSampler
{
	public static void SampleBones(AnimationClip clip, Skeleton skeleton, float time, Pose pose)
	{
		ArgumentNullException.ThrowIfNull(clip);
		ArgumentNullException.ThrowIfNull(skeleton);
		ArgumentNullException.ThrowIfNull(pose);

		pose.SetToBindPose(skeleton);

		var sampleTime = clip.NormalizeTime(time);
		var remap = BoneRemap.Get(clip, skeleton);
		var tracks = clip.TransformTracks;
		for (var i = 0; i < tracks.Length; i++)
		{
			var bone = remap.TrackToBone[i];
			if (bone < 0 || bone >= pose.BoneCount)
			{
				continue;
			}

			var track = tracks[i];
			var bindPose = skeleton.BindPoseLocal[bone];
			var positionCursor = 0;
			var rotationCursor = 0;
			var scaleCursor = 0;
			pose.Bones[bone] = new BoneTransform(
				track.Position.Evaluate(sampleTime, ref positionCursor, bindPose.Position),
				track.Rotation.Evaluate(sampleTime, ref rotationCursor, bindPose.Rotation),
				track.Scale.Evaluate(sampleTime, ref scaleCursor, bindPose.Scale));
		}
	}
}
