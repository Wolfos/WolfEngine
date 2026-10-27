using System.Numerics;
using WolfEngine.Animation;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class AnimationClipPoseSamplerTests
{
	private Skeleton _skeleton = null!;

	[SetUp]
	public void SetUp()
	{
		_skeleton = new Skeleton(
			"rig",
			["root", "arm", "leg"],
			[-1, 0, 0],
			[
				BoneTransform.Identity,
				new BoneTransform(new Vector3(1.0f, 0.0f, 0.0f), Quaternion.Identity, Vector3.One),
				new BoneTransform(new Vector3(0.0f, -1.0f, 0.0f), Quaternion.Identity, Vector3.One)
			],
			[Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity]);
	}

	[Test]
	public void SampleBones_InterpolatesTrackedBones()
	{
		var clip = CreateClip(loop: false);
		var pose = new Pose(_skeleton.BoneCount);

		AnimationClipPoseSampler.SampleBones(clip, _skeleton, 0.5f, pose);

		var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.25f);
		Assert.That(MathF.Abs(Quaternion.Dot(pose.Bones[1].Rotation, expected)), Is.EqualTo(1.0f).Within(1e-4f));
	}

	[Test]
	public void SampleBones_KeepsTheBindPoseForEmptyChannelsAndUntrackedBones()
	{
		var clip = CreateClip(loop: false);
		var pose = new Pose(_skeleton.BoneCount);

		AnimationClipPoseSampler.SampleBones(clip, _skeleton, 0.5f, pose);

		Assert.That(pose.Bones[1].Position, Is.EqualTo(new Vector3(1.0f, 0.0f, 0.0f)));
		Assert.That(pose.Bones[1].Scale, Is.EqualTo(Vector3.One));
		Assert.That(pose.Bones[2].Position, Is.EqualTo(new Vector3(0.0f, -1.0f, 0.0f)));
	}

	[Test]
	public void SampleBones_ResetsBonesFromAPreviousSample()
	{
		var clip = CreateClip(loop: false);
		var pose = new Pose(_skeleton.BoneCount);
		pose.Bones[2] = new BoneTransform(new Vector3(9.0f), Quaternion.Identity, Vector3.One);

		AnimationClipPoseSampler.SampleBones(clip, _skeleton, 0.0f, pose);

		Assert.That(pose.Bones[2].Position, Is.EqualTo(new Vector3(0.0f, -1.0f, 0.0f)));
	}

	[Test]
	public void SampleBones_WrapsLoopingClips()
	{
		var clip = CreateClip(loop: true);
		var wrapped = new Pose(_skeleton.BoneCount);
		var direct = new Pose(_skeleton.BoneCount);

		AnimationClipPoseSampler.SampleBones(clip, _skeleton, 1.25f, wrapped);
		AnimationClipPoseSampler.SampleBones(clip, _skeleton, 0.25f, direct);

		Assert.That(wrapped.Bones[1].Rotation, Is.EqualTo(direct.Bones[1].Rotation));
	}

	[Test]
	public void SampleBones_IgnoresTracksForBonesTheSkeletonLacks()
	{
		var clip = new AnimationClip(
			"other",
			1.0f,
			30.0f,
			false,
			[
				new TransformTrack(
					AnimationBinding.ForBone("tail"),
					new Vector3Curve([0.0f], [new Vector3(5.0f)]),
					QuaternionCurve.Empty,
					Vector3Curve.Empty)
			],
			[],
			string.Empty,
			[]);
		var pose = new Pose(_skeleton.BoneCount);

		Assert.DoesNotThrow(() => AnimationClipPoseSampler.SampleBones(clip, _skeleton, 0.0f, pose));
		Assert.That(pose.Bones[1].Position, Is.EqualTo(new Vector3(1.0f, 0.0f, 0.0f)));
	}

	private static AnimationClip CreateClip(bool loop)
	{
		var rotation = new QuaternionCurve(
			[0.0f, 1.0f],
			[Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f)]);

		return new AnimationClip(
			"wave",
			1.0f,
			30.0f,
			loop,
			[new TransformTrack(AnimationBinding.ForBone("arm"), Vector3Curve.Empty, rotation, Vector3Curve.Empty)],
			[],
			"rig",
			[]);
	}
}
