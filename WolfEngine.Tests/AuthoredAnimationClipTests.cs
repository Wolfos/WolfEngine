using System.Numerics;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class AuthoredAnimationClipTests
{
	private string _directory = string.Empty;

	[SetUp]
	public void SetUp()
	{
		_directory = Path.Combine(Path.GetTempPath(), $"wolf-authored-clip-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_directory);
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(_directory))
		{
			Directory.Delete(_directory, recursive: true);
		}
	}

	[Test]
	public void Json_RoundTripsKeysSettingsAndPropertyTracks()
	{
		var written = CreateClip();
		var path = Path.Combine(_directory, "Swing.anim.json");

		AuthoredAnimationClipJson.Write(path, written);
		var read = AuthoredAnimationClipJson.Read(path);

		Assert.That(AuthoredAnimationClipJson.Serialize(read), Is.EqualTo(AuthoredAnimationClipJson.Serialize(written)));
		Assert.That(read.Skeleton.NodeId, Is.EqualTo(written.Skeleton.NodeId));
		Assert.That(read.PreviewModelId, Is.EqualTo(written.PreviewModelId));
		Assert.That(read.TransformTracks[0].Rotation.Values[1], Is.EqualTo(written.TransformTracks[0].Rotation.Values[1]));
		Assert.That(read.PropertyTracks[0].OutTangents, Is.EqualTo(written.PropertyTracks[0].OutTangents));
		Assert.That(File.Exists(path + ".tmp"), Is.False);
	}

	[Test]
	public void Json_WritesVectorsAsNumberArrays()
	{
		var json = AuthoredAnimationClipJson.Serialize(CreateClip());

		Assert.That(json, Does.Not.Contain("\"X\""));
		Assert.That(json, Does.Not.Contain("IsIdentity"));
	}

	[Test]
	public void Compile_ConvertsFramesToSeconds()
	{
		var clip = CreateClip();

		var compiled = clip.Compile("Swing", "rig", [BoneTransform.Identity]);
		var runtime = compiled.ToClip();

		Assert.That(runtime.Name, Is.EqualTo("Swing"));
		Assert.That(runtime.Duration, Is.EqualTo(1.0f));
		Assert.That(runtime.FramesPerSecond, Is.EqualTo(30.0f));
		Assert.That(runtime.SourceSkeletonName, Is.EqualTo("rig"));
		Assert.That(runtime.TransformTracks[0].Binding, Is.EqualTo(AnimationBinding.ForBone("arm")));
		Assert.That(runtime.TransformTracks[0].Rotation.Times, Is.EqualTo(new[] { 0.0f, 0.5f }));
		Assert.That(runtime.TransformTracks[0].Scale.IsEmpty, Is.True);
		Assert.That(runtime.PropertyTracks[0].Binding, Is.EqualTo(AnimationBinding.ForProperty("Light", "Intensity")));
	}

	[Test]
	public void Compile_SamplesLinearlyBetweenFrames()
	{
		var clip = CreateClip();

		var track = clip.Compile("Swing", string.Empty, []).ToClip().TransformTracks[0];
		var cursor = 0;
		var position = track.Position.Evaluate(0.25f, ref cursor, Vector3.Zero);

		Assert.That(position.X, Is.EqualTo(0.5f).Within(1e-5f));
	}

	[Test]
	public void Validate_RejectsUnsortedFrames()
	{
		var clip = CreateClip();
		clip.TransformTracks[0].Position.Frames = [15, 0];

		Assert.Throws<InvalidDataException>(clip.Validate);
	}

	[Test]
	public void Validate_RejectsKeysPastTheEnd()
	{
		var clip = CreateClip();
		clip.TransformTracks[0].Position.Frames = [0, 31];

		Assert.Throws<InvalidDataException>(clip.Validate);
	}

	[Test]
	public void Validate_RejectsHermiteKeys()
	{
		var clip = CreateClip();
		clip.TransformTracks[0].Position.Interpolation = CurveInterpolation.CubicHermite;

		Assert.Throws<InvalidDataException>(clip.Validate);
	}

	[Test]
	public void Validate_RejectsDuplicateTracks()
	{
		var clip = CreateClip();
		clip.TransformTracks.Add(AuthoredTransformTrack.ForBone("arm"));

		Assert.Throws<InvalidDataException>(clip.Validate);
	}

	[Test]
	public void Read_ReportsThePathOfAnInvalidClip()
	{
		var clip = CreateClip();
		clip.FramesPerSecond = 0;
		var path = Path.Combine(_directory, "Broken.anim.json");
		File.WriteAllText(path, AuthoredAnimationClipJson.Serialize(clip));

		var exception = Assert.Throws<InvalidDataException>(() => AuthoredAnimationClipJson.Read(path));

		Assert.That(exception!.Message, Does.Contain("Broken.anim.json"));
	}

	[Test]
	public void GetClipName_StripsTheDoubleExtension()
	{
		Assert.That(AuthoredAnimationClipFile.GetClipName("Assets/Anim/Sword Attack.anim.json"), Is.EqualTo("Sword Attack"));
		Assert.That(AuthoredAnimationClipJson.IsAuthoredClipPath("Assets/Graph.animgraph.json"), Is.False);
	}

	private static AuthoredAnimationClipFile CreateClip()
	{
		var arm = AuthoredTransformTrack.ForBone("arm");
		arm.Position.Set(0, Vector3.Zero);
		arm.Position.Set(15, new Vector3(1.0f, 0.0f, 0.0f));
		arm.Rotation.Set(0, Quaternion.Identity);
		arm.Rotation.Set(15, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.75f));

		return new AuthoredAnimationClipFile
		{
			Skeleton = new AssetRef<Skeleton>
			{
				NodeId = Guid.NewGuid()
			},
			PreviewModelId = Guid.NewGuid(),
			FramesPerSecond = 30,
			LengthFrames = 30,
			Loop = false,
			TransformTracks = [arm],
			PropertyTracks =
			[
				new AuthoredPropertyTrack
				{
					Path = "Light",
					Property = "Intensity",
					Interpolation = CurveInterpolation.CubicHermite,
					Times = [0.0f, 0.4f],
					Values = [1.0f, 3.0f],
					InTangents = [0.0f, 0.1f],
					OutTangents = [0.2f, 0.0f]
				}
			]
		};
	}
}
