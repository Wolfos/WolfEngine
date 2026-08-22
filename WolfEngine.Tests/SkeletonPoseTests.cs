using System.Numerics;
using WolfEngine.Animation;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class SkeletonPoseTests
{
	/// <summary>
	/// A three-bone chain along +Y, each bone one unit above its parent, with inverse bind matrices
	/// derived from that rest pose.
	/// </summary>
	private static Skeleton CreateChain()
	{
		var bindPose = new[]
		{
			new BoneTransform(Vector3.Zero, Quaternion.Identity, Vector3.One),
			new BoneTransform(Vector3.UnitY, Quaternion.Identity, Vector3.One),
			new BoneTransform(Vector3.UnitY, Quaternion.Identity, Vector3.One)
		};

		var inverseBind = new Matrix4x4[3];
		var model = Matrix4x4.Identity;
		for (var i = 0; i < bindPose.Length; i++)
		{
			model = i == 0 ? bindPose[i].ToMatrix() : bindPose[i].ToMatrix() * model;
			Matrix4x4.Invert(model, out inverseBind[i]);
		}

		return new Skeleton("chain", ["root", "middle", "tip"], [-1, 0, 1], bindPose, inverseBind);
	}

	[Test]
	public void ComputeSkinningMatrices_AtBindPoseProducesIdentity()
	{
		var skeleton = CreateChain();
		var pose = skeleton.CreatePose();
		var skinning = new Matrix4x4[skeleton.BoneCount];

		pose.ComputeSkinningMatrices(skeleton, skinning);

		for (var i = 0; i < skinning.Length; i++)
		{
			Assert.That(skinning[i].Translation.Length(), Is.EqualTo(0.0f).Within(1e-5f), $"bone {i} translation");
			Assert.That(Vector3.Transform(new Vector3(1.0f, 2.0f, 3.0f), skinning[i]),
				Is.EqualTo(new Vector3(1.0f, 2.0f, 3.0f)).Using<Vector3>((a, b) => Vector3.Distance(a, b) < 1e-4f),
				$"bone {i} must leave bind-pose geometry where it is");
		}
	}

	[Test]
	public void ComputeSkinningMatrices_TranslatingTheRootMovesEveryDescendant()
	{
		var skeleton = CreateChain();
		var pose = skeleton.CreatePose();
		pose.Bones[0].Position = new Vector3(5.0f, 0.0f, 0.0f);

		var skinning = new Matrix4x4[skeleton.BoneCount];
		pose.ComputeSkinningMatrices(skeleton, skinning);

		for (var i = 0; i < skinning.Length; i++)
		{
			var moved = Vector3.Transform(Vector3.Zero, skinning[i]);
			Assert.That(moved, Is.EqualTo(new Vector3(5.0f, 0.0f, 0.0f))
				.Using<Vector3>((a, b) => Vector3.Distance(a, b) < 1e-4f), $"bone {i}");
		}
	}

	/// <summary>
	/// Rotating the middle bone 90 degrees about Z must swing the tip from directly above the middle
	/// to directly beside it, which is the check that the parent chain composes in the right order.
	/// </summary>
	[Test]
	public void ComputeSkinningMatrices_RotatingAParentSwingsTheChild()
	{
		var skeleton = CreateChain();
		var pose = skeleton.CreatePose();
		pose.Bones[1].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f);

		var skinning = new Matrix4x4[skeleton.BoneCount];
		pose.ComputeSkinningMatrices(skeleton, skinning);

		// The tip's rest position is (0, 2, 0); after the swing it should sit at (-1, 1, 0).
		var tip = Vector3.Transform(new Vector3(0.0f, 2.0f, 0.0f), skinning[2]);
		Assert.That(tip, Is.EqualTo(new Vector3(-1.0f, 1.0f, 0.0f))
			.Using<Vector3>((a, b) => Vector3.Distance(a, b) < 1e-4f));
	}

	[Test]
	public void Skeleton_RejectsBonesDeclaredBeforeTheirParent()
	{
		var bindPose = new[] { BoneTransform.Identity, BoneTransform.Identity };
		var inverseBind = new[] { Matrix4x4.Identity, Matrix4x4.Identity };

		Assert.That(
			() => new Skeleton("bad", ["child", "parent"], [1, -1], bindPose, inverseBind),
			Throws.ArgumentException,
			"pose evaluation reads a parent's model matrix in a single forward pass");
	}

	[Test]
	public void Skeleton_RejectsMismatchedBoneArrayLengths()
	{
		Assert.That(
			() => new Skeleton("bad", ["a", "b"], [-1], [BoneTransform.Identity], [Matrix4x4.Identity]),
			Throws.ArgumentException);
	}

	[Test]
	public void BoneRemap_ResolvesTracksByNameAndReportsMisses()
	{
		var skeleton = CreateChain();
		var clip = new AnimationClip(
			"clip",
			1.0f,
			30.0f,
			true,
			[
				new TransformTrack(AnimationBinding.ForBone("middle"), Vector3Curve.Empty, QuaternionCurve.Empty, Vector3Curve.Empty),
				new TransformTrack(AnimationBinding.ForBone("not-on-this-rig"), Vector3Curve.Empty, QuaternionCurve.Empty, Vector3Curve.Empty),
				new TransformTrack(AnimationBinding.ForProperty("Door", string.Empty), Vector3Curve.Empty, QuaternionCurve.Empty, Vector3Curve.Empty)
			],
			[],
			"chain",
			skeleton.BindPoseLocal);

		var remap = BoneRemap.Get(clip, skeleton);

		Assert.Multiple(() =>
		{
			Assert.That(remap.TrackToBone[0], Is.EqualTo(1), "'middle' is bone index 1");
			Assert.That(remap.TrackToBone[1], Is.EqualTo(-1), "unknown bone names do not resolve");
			Assert.That(remap.TrackToBone[2], Is.EqualTo(-1), "non-bone tracks never map to a bone");
			Assert.That(remap.TrackToTransformSlot[2], Is.EqualTo(0), "the single non-bone track takes the first slot");
			Assert.That(remap.MatchedBoneTrackCount, Is.EqualTo(1));
			Assert.That(remap.UnmatchedBoneTrackCount, Is.EqualTo(1));
		});
	}

	[Test]
	public void BoneRemap_IsCachedPerClipAndSkeletonPair()
	{
		var skeleton = CreateChain();
		var clip = new AnimationClip("clip", 1.0f, 30.0f, true, [], [], string.Empty, []);

		var first = BoneRemap.Get(clip, skeleton);
		var second = BoneRemap.Get(clip, skeleton);

		Assert.That(second, Is.SameAs(first));
	}

	/// <summary>
	/// An exporter that emits rotation keys but no translation keys is common. The missing channel
	/// has to fall back to the bind pose rather than to zero, which would collapse the bone onto its
	/// parent.
	/// </summary>
	[Test]
	public void SingleClipPoseSource_FallsBackToBindPosePerChannel()
	{
		var skeleton = CreateChain();
		var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f);
		var clip = new AnimationClip(
			"clip",
			1.0f,
			30.0f,
			true,
			[
				new TransformTrack(
					AnimationBinding.ForBone("middle"),
					Vector3Curve.Empty,
					new QuaternionCurve([0.0f], [rotation]),
					Vector3Curve.Empty)
			],
			[],
			"chain",
			skeleton.BindPoseLocal);

		var source = new SingleClipPoseSource(clip, skeleton);
		var pose = clip.CreatePose(skeleton);
		source.Evaluate(0.0f, pose);

		Assert.Multiple(() =>
		{
			Assert.That(pose.Bones[1].Position, Is.EqualTo(Vector3.UnitY)
				.Using<Vector3>((a, b) => Vector3.Distance(a, b) < 1e-5f), "translation falls back to the bind pose");
			Assert.That(pose.Bones[1].Scale, Is.EqualTo(Vector3.One)
				.Using<Vector3>((a, b) => Vector3.Distance(a, b) < 1e-5f), "scale falls back to the bind pose");
			Assert.That(Quaternion.Dot(pose.Bones[1].Rotation, rotation), Is.EqualTo(1.0f).Within(1e-4f));
		});
	}

	[Test]
	public void SingleClipPoseSource_AdvancesAndWrapsPlaybackTime()
	{
		var skeleton = CreateChain();
		var clip = new AnimationClip("clip", 1.0f, 30.0f, true, [], [], string.Empty, []);
		var source = new SingleClipPoseSource(clip, skeleton) { Speed = 1.0f };
		var pose = clip.CreatePose(skeleton);

		source.Evaluate(0.75f, pose);
		Assert.That(source.Time, Is.EqualTo(0.75f).Within(1e-5f));

		source.Evaluate(0.5f, pose);
		Assert.That(source.Time, Is.EqualTo(0.25f).Within(1e-5f), "playback wraps rather than running past the end");
	}

	[Test]
	public void Pose_BlendInterpolatesBonesTransformsAndValues()
	{
		var a = new Pose(1, 1, 1);
		var b = new Pose(1, 1, 1);
		var destination = new Pose(1, 1, 1);

		a.Bones[0] = new BoneTransform(Vector3.Zero, Quaternion.Identity, Vector3.One);
		b.Bones[0] = new BoneTransform(new Vector3(4.0f, 0.0f, 0.0f), Quaternion.Identity, Vector3.One * 3.0f);
		a.Transforms[0] = BoneTransform.Identity;
		b.Transforms[0] = new BoneTransform(new Vector3(0.0f, 8.0f, 0.0f), Quaternion.Identity, Vector3.One);
		a.Values[0] = 0.0f;
		b.Values[0] = 10.0f;

		Pose.Blend(a, b, 0.25f, destination);

		Assert.Multiple(() =>
		{
			Assert.That(destination.Bones[0].Position.X, Is.EqualTo(1.0f).Within(1e-5f));
			Assert.That(destination.Bones[0].Scale.X, Is.EqualTo(1.5f).Within(1e-5f));
			Assert.That(destination.Transforms[0].Position.Y, Is.EqualTo(2.0f).Within(1e-5f));
			Assert.That(destination.Values[0], Is.EqualTo(2.5f).Within(1e-5f));
		});
	}
}
