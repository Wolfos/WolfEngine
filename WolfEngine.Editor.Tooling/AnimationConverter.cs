using System.Numerics;
using Silk.NET.Assimp;
using WolfEngine.Animation;

namespace WolfEngine.Importing;

/// <summary>
/// Converts Assimp animation channels into engine clips.
/// </summary>
/// <remarks>
/// A channel targeting a skeleton bone becomes a bone-bound transform track; a channel targeting
/// anything else becomes a property-bound transform track addressed by node name. That is the whole
/// of the non-skeletal animation path — an animated door or turret arrives through the same clip,
/// the same sampler and the same blending as a character, with no separate system.
/// </remarks>
internal static class AnimationConverter
{
	/// <summary>
	/// Assimp leaves ticks-per-second at zero for formats that do not state a rate. Assimp's own
	/// default for that case is 25.
	/// </summary>
	private const double DefaultTicksPerSecond = 25.0;

	internal static unsafe void Convert(
		Scene* scene,
		SkeletonBuildResult? skeleton,
		List<ImportedAnimation> output)
	{
		if (scene is null || scene->MNumAnimations == 0)
		{
			return;
		}

		for (var animationIndex = 0; animationIndex < scene->MNumAnimations; animationIndex++)
		{
			var animation = scene->MAnimations[animationIndex];
			if (animation is null || animation->MNumChannels == 0)
			{
				continue;
			}

			var ticksPerSecond = animation->MTicksPerSecond > 0.0
				? animation->MTicksPerSecond
				: DefaultTicksPerSecond;
			var duration = (float)(animation->MDuration / ticksPerSecond);

			var transformTracks = new List<TransformTrack>((int)animation->MNumChannels);
			var boneTrackCount = 0;

			for (var channelIndex = 0; channelIndex < animation->MNumChannels; channelIndex++)
			{
				var channel = animation->MChannels[channelIndex];
				if (channel is null)
				{
					continue;
				}

				var nodeName = channel->MNodeName.AsString;
				if (string.IsNullOrEmpty(nodeName))
				{
					continue;
				}

				var isBone = skeleton is not null && skeleton.BoneIndicesByName.ContainsKey(nodeName);
				var binding = isBone
					? AnimationBinding.ForBone(nodeName)
					: AnimationBinding.ForProperty(nodeName, string.Empty);

				if (isBone)
				{
					boneTrackCount++;
				}

				transformTracks.Add(new TransformTrack(
					binding,
					ReadVector3Curve(channel->MPositionKeys, channel->MNumPositionKeys, ticksPerSecond),
					ReadQuaternionCurve(channel->MRotationKeys, channel->MNumRotationKeys, ticksPerSecond),
					ReadVector3Curve(channel->MScalingKeys, channel->MNumScalingKeys, ticksPerSecond)));
			}

			if (transformTracks.Count == 0)
			{
				continue;
			}

			var name = string.IsNullOrWhiteSpace(animation->MName.AsString)
				? $"Animation_{animationIndex}"
				: animation->MName.AsString;

			output.Add(new ImportedAnimation(
				name,
				duration,
				(float)ticksPerSecond,
				boneTrackCount > 0 && skeleton is not null ? 0 : -1,
				transformTracks.ToArray(),
				[]));
		}
	}

	private static unsafe Vector3Curve ReadVector3Curve(VectorKey* keys, uint keyCount, double ticksPerSecond)
	{
		if (keys is null || keyCount == 0)
		{
			return Vector3Curve.Empty;
		}

		var times = new float[keyCount];
		var values = new Vector3[keyCount];
		for (var i = 0; i < keyCount; i++)
		{
			times[i] = (float)(keys[i].MTime / ticksPerSecond);
			values[i] = keys[i].MValue;
		}

		return new Vector3Curve(times, values, CurveInterpolation.Linear);
	}

	private static unsafe QuaternionCurve ReadQuaternionCurve(QuatKey* keys, uint keyCount, double ticksPerSecond)
	{
		if (keys is null || keyCount == 0)
		{
			return QuaternionCurve.Empty;
		}

		var times = new float[keyCount];
		var values = new Quaternion[keyCount];
		for (var i = 0; i < keyCount; i++)
		{
			times[i] = (float)(keys[i].MTime / ticksPerSecond);
			var value = keys[i].MValue;
			// Assimp stores quaternions w-first; System.Numerics is w-last.
			values[i] = new Quaternion(value.X, value.Y, value.Z, value.W);
		}

		return new QuaternionCurve(times, values, CurveInterpolation.Linear);
	}
}
