using System;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Animation;

/// <summary>
/// Animates a local TRS transform. The binding decides what that transform lands on: a skeleton
/// bone, or an arbitrary entity in the hierarchy. Keeping one track type for both is what lets an
/// animated door or turret ride through the same clips, sampler and blending as a character,
/// rather than needing a parallel system.
/// </summary>
public sealed class TransformTrack
{
	public TransformTrack(AnimationBinding binding, Vector3Curve position, QuaternionCurve rotation, Vector3Curve scale)
	{
		Binding = binding;
		Position = position ?? Vector3Curve.Empty;
		Rotation = rotation ?? QuaternionCurve.Empty;
		Scale = scale ?? Vector3Curve.Empty;
	}

	public AnimationBinding Binding { get; }
	public Vector3Curve Position { get; }
	public QuaternionCurve Rotation { get; }
	public Vector3Curve Scale { get; }

	public bool IsBoneTrack => Binding.Kind == AnimationBindingKind.Bone;
}

/// <summary>
/// Animates a single scalar that is not part of a transform, such as a light's intensity or a
/// material parameter.
/// </summary>
public sealed class PropertyTrack
{
	public PropertyTrack(AnimationBinding binding, FloatCurve curve)
	{
		Binding = binding;
		Curve = curve ?? FloatCurve.Empty;
	}

	public AnimationBinding Binding { get; }
	public FloatCurve Curve { get; }
}

/// <summary>
/// A block of animation data. Independent of any particular skeleton instance: binding happens at
/// runtime through <see cref="BoneRemap"/>.
/// </summary>
[RuntimeAsset(AssetType.AnimationClip, typeof(ImportedAnimationClipAssetFile), typeof(IAnimationClipRuntimeAssetResolver))]
public sealed class AnimationClip
{
	public AnimationClip(
		string name,
		float duration,
		float framesPerSecond,
		bool loop,
		TransformTrack[] transformTracks,
		PropertyTrack[] propertyTracks,
		string sourceSkeletonName,
		BoneTransform[] sourceBindPoseLocal)
	{
		Name = name ?? string.Empty;
		Duration = MathF.Max(0.0f, duration);
		FramesPerSecond = framesPerSecond > 0.0f ? framesPerSecond : 30.0f;
		Loop = loop;
		TransformTracks = transformTracks ?? Array.Empty<TransformTrack>();
		PropertyTracks = propertyTracks ?? Array.Empty<PropertyTrack>();
		SourceSkeletonName = sourceSkeletonName ?? string.Empty;
		SourceBindPoseLocal = sourceBindPoseLocal ?? Array.Empty<BoneTransform>();

		var nonBoneCount = 0;
		for (var i = 0; i < TransformTracks.Length; i++)
		{
			if (TransformTracks[i].IsBoneTrack == false)
			{
				nonBoneCount++;
			}
		}

		NonBoneTransformTrackCount = nonBoneCount;
	}

	public string Name { get; }
	public float Duration { get; }
	public float FramesPerSecond { get; }
	public bool Loop { get; }
	public TransformTrack[] TransformTracks { get; }
	public PropertyTrack[] PropertyTracks { get; }

	/// <summary>Number of transform tracks that target something other than a skeleton bone.</summary>
	public int NonBoneTransformTrackCount { get; }

	/// <summary>
	/// Name of the skeleton this clip was authored against, and that skeleton's bind pose.
	/// Retargeting needs both to express a track as a delta from its source rest pose before
	/// reapplying it to a differently-proportioned rig; discarding them is what would lock clips
	/// to one skeleton permanently.
	/// </summary>
	public string SourceSkeletonName { get; }

	public BoneTransform[] SourceBindPoseLocal { get; }

	/// <summary>Wraps or clamps a playback time into the clip's range.</summary>
	public float NormalizeTime(float time)
	{
		if (Duration <= 0.0f)
		{
			return 0.0f;
		}

		if (Loop == false)
		{
			return Math.Clamp(time, 0.0f, Duration);
		}

		var wrapped = time % Duration;
		return wrapped < 0.0f ? wrapped + Duration : wrapped;
	}

	/// <summary>Creates a pose sized to hold everything this clip can drive on the given skeleton.</summary>
	public Pose CreatePose(Skeleton skeleton)
	{
		ArgumentNullException.ThrowIfNull(skeleton);
		var pose = new Pose(skeleton.BoneCount, NonBoneTransformTrackCount, PropertyTracks.Length);
		pose.SetToBindPose(skeleton);
		return pose;
	}
}
