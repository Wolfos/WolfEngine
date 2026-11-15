using System.Numerics;
using WolfEngine.Animation;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class AuthoredClipConversionTests
{
	[Test]
	public void FrameAlignedLinearTrack_IsCopiedAsItIs()
	{
		var rotation = new QuaternionCurve(
			[0.0f, 0.5f, 1.0f],
			[Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.0f), Quaternion.Identity]);
		var clip = Clip(1.0f, new TransformTrack(AnimationBinding.ForBone("arm"), Vector3Curve.Empty, rotation, Vector3Curve.Empty));

		var authored = AuthoredAnimationClipFile.FromClip(clip, 30);

		var track = authored.FindBoneTrack("arm")!;
		Assert.That(track.Rotation.Frames, Is.EqualTo(new[] { 0, 15, 30 }));
		Assert.That(track.Rotation.Values[1], Is.EqualTo(rotation.Values[1]));
		Assert.That(track.Position.Frames, Is.Empty);
	}

	[Test]
	public void ConstantTrack_KeepsStepInterpolation()
	{
		var position = new Vector3Curve([0.0f, 1.0f], [Vector3.Zero, Vector3.One], CurveInterpolation.Constant);
		var clip = Clip(1.0f, new TransformTrack(AnimationBinding.ForBone("arm"), position, QuaternionCurve.Empty, Vector3Curve.Empty));

		var track = AuthoredAnimationClipFile.FromClip(clip, 30).FindBoneTrack("arm")!;

		Assert.That(track.Position.Interpolation, Is.EqualTo(CurveInterpolation.Constant));
		Assert.That(track.Position.Frames, Is.EqualTo(new[] { 0, 30 }));
	}

	[Test]
	public void KeysBetweenFrames_AreResampledOnEveryFrame()
	{
		// 25 ticks per second does not land on 30 FPS frames.
		var position = new Vector3Curve([0.0f, 0.04f, 0.2f], [Vector3.Zero, new Vector3(1.0f, 0.0f, 0.0f), new Vector3(5.0f, 0.0f, 0.0f)]);
		var clip = Clip(0.2f, new TransformTrack(AnimationBinding.ForBone("arm"), position, QuaternionCurve.Empty, Vector3Curve.Empty));

		var track = AuthoredAnimationClipFile.FromClip(clip, 30).FindBoneTrack("arm")!;

		Assert.That(track.Position.Frames, Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6 }));
		Assert.That(track.Position.Interpolation, Is.EqualTo(CurveInterpolation.Linear));
		var cursor = 0;
		Assert.That(track.Position.Values[3].X, Is.EqualTo(position.Evaluate(0.1f, ref cursor, Vector3.Zero).X).Within(1e-4f));
	}

	[Test]
	public void OneMisalignedChannel_ResamplesTheWholeBone()
	{
		var position = new Vector3Curve([0.0f, 0.5f], [Vector3.Zero, Vector3.One]);
		var rotation = new QuaternionCurve([0.0f, 0.51f], [Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.0f)]);
		var clip = Clip(1.0f, new TransformTrack(AnimationBinding.ForBone("arm"), position, rotation, Vector3Curve.Empty));

		var track = AuthoredAnimationClipFile.FromClip(clip, 10).FindBoneTrack("arm")!;

		Assert.That(track.Position.Frames, Has.Count.EqualTo(11));
		Assert.That(track.Rotation.Frames, Has.Count.EqualTo(11));
		Assert.That(track.Scale.Frames, Is.Empty);
	}

	[Test]
	public void HermiteTrack_IsResampled()
	{
		var position = new Vector3Curve(
			[0.0f, 1.0f],
			[Vector3.Zero, Vector3.One],
			CurveInterpolation.CubicHermite,
			[Vector3.Zero, Vector3.Zero],
			[Vector3.Zero, Vector3.Zero]);
		var clip = Clip(1.0f, new TransformTrack(AnimationBinding.ForBone("arm"), position, QuaternionCurve.Empty, Vector3Curve.Empty));

		var authored = AuthoredAnimationClipFile.FromClip(clip, 4);

		Assert.That(authored.FindBoneTrack("arm")!.Position.Frames, Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));
		Assert.DoesNotThrow(authored.Validate);
	}

	[Test]
	public void Conversion_KeepsLengthLoopPropertyTracksAndNonBoneTracks()
	{
		var property = new PropertyTrack(
			AnimationBinding.ForProperty("Light", "Intensity"),
			new FloatCurve([0.0f, 0.3f], [1.0f, 2.0f], CurveInterpolation.CubicHermite, [0.0f, 0.0f], [0.5f, 0.0f]));
		var door = new TransformTrack(
			AnimationBinding.ForProperty("Door", string.Empty),
			new Vector3Curve([0.0f], [Vector3.One]),
			QuaternionCurve.Empty,
			Vector3Curve.Empty);
		var clip = new AnimationClip("take", 1.25f, 24.0f, false, [door], [property], "rig", []);

		var authored = AuthoredAnimationClipFile.FromClip(clip, 24);

		Assert.That(authored.LengthFrames, Is.EqualTo(30));
		Assert.That(authored.Loop, Is.False);
		Assert.That(authored.PropertyTracks.Single().OutTangents, Is.EqualTo(new[] { 0.5f, 0.0f }));
		Assert.That(authored.TransformTracks.Single().Kind, Is.EqualTo(AnimationBindingKind.Property));
		Assert.DoesNotThrow(authored.Validate);
	}

	[Test]
	public void DuplicateTracks_KeepTheFirst()
	{
		var first = new TransformTrack(AnimationBinding.ForBone("arm"), new Vector3Curve([0.0f], [Vector3.One]), QuaternionCurve.Empty, Vector3Curve.Empty);
		var second = new TransformTrack(AnimationBinding.ForBone("arm"), new Vector3Curve([0.0f], [Vector3.Zero]), QuaternionCurve.Empty, Vector3Curve.Empty);
		var clip = new AnimationClip("take", 1.0f, 30.0f, true, [first, second], [], "rig", []);

		var authored = AuthoredAnimationClipFile.FromClip(clip, 30);

		Assert.That(authored.TransformTracks, Has.Count.EqualTo(1));
		Assert.That(authored.TransformTracks[0].Position.Values[0], Is.EqualTo(Vector3.One));
	}

	[Test]
	public void ConvertedClip_PlaysLikeTheOriginal()
	{
		var rotation = new QuaternionCurve(
			[0.0f, 0.37f, 1.0f],
			[Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.2f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f)]);
		var original = Clip(1.0f, new TransformTrack(AnimationBinding.ForBone("arm"), Vector3Curve.Empty, rotation, Vector3Curve.Empty));

		var converted = AuthoredAnimationClipFile.FromClip(original, 30).Compile("take", "rig", []).ToClip();

		// On frames the resampled clip matches the source exactly.
		for (var frame = 0; frame <= 30; frame++)
		{
			var time = frame / 30.0f;
			var originalCursor = 0;
			var convertedCursor = 0;
			var expected = original.TransformTracks[0].Rotation.Evaluate(time, ref originalCursor, Quaternion.Identity);
			var actual = converted.TransformTracks[0].Rotation.Evaluate(time, ref convertedCursor, Quaternion.Identity);
			Assert.That(MathF.Abs(Quaternion.Dot(expected, actual)), Is.EqualTo(1.0f).Within(1e-5f), $"frame {frame}");
		}
	}

	private static AnimationClip Clip(float duration, TransformTrack track) =>
		new("take", duration, 30.0f, true, [track], [], "rig", []);
}
