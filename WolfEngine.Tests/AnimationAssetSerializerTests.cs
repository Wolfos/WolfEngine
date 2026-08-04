using System.Numerics;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class AnimationAssetSerializerTests
{
	private string _directory = string.Empty;

	[SetUp]
	public void SetUp()
	{
		_directory = Path.Combine(Path.GetTempPath(), $"wolf-anim-{Guid.NewGuid():N}");
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
	public void SkeletonSerializer_RoundTripsBoneHierarchyAndBindPose()
	{
		var written = new ImportedSkeletonAssetFile
		{
			Name = "rig",
			BoneNames = ["root", "child"],
			ParentIndices = [-1, 0],
			BindPoseLocal =
			[
				new BoneTransform(new Vector3(1, 2, 3), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f), new Vector3(1, 1, 1)),
				new BoneTransform(new Vector3(0, 4, 0), Quaternion.Identity, new Vector3(2, 2, 2))
			],
			InverseBindMatrices = [Matrix4x4.CreateTranslation(1, 2, 3), Matrix4x4.CreateScale(0.5f)]
		};

		var path = Path.Combine(_directory, "rig.skel.bin");
		SkeletonSerializer.Write(path, written);
		var read = SkeletonSerializer.Read(path);

		Assert.Multiple(() =>
		{
			Assert.That(read.Name, Is.EqualTo("rig"));
			Assert.That(read.BoneNames, Is.EqualTo(written.BoneNames));
			Assert.That(read.ParentIndices, Is.EqualTo(written.ParentIndices));
			Assert.That(read.InverseBindMatrices, Is.EqualTo(written.InverseBindMatrices));
			Assert.That(read.BindPoseLocal[0].Position, Is.EqualTo(new Vector3(1, 2, 3)));
			Assert.That(read.BindPoseLocal[1].Scale, Is.EqualTo(new Vector3(2, 2, 2)));
			Assert.That(read.ToSkeleton().BoneCount, Is.EqualTo(2));
		});
	}

	[Test]
	public void AnimationClipSerializer_RoundTripsBoneAndPropertyTracks()
	{
		var written = new ImportedAnimationClipAssetFile
		{
			Name = "walk",
			Duration = 1.25f,
			FramesPerSecond = 24.0f,
			Loop = true,
			SourceSkeletonName = "rig",
			SourceBindPoseLocal = [BoneTransform.Identity],
			TransformTracks =
			[
				new TransformTrack(
					AnimationBinding.ForBone("root"),
					new Vector3Curve([0.0f, 1.0f], [Vector3.Zero, Vector3.UnitX]),
					new QuaternionCurve([0.0f], [Quaternion.Identity]),
					Vector3Curve.Empty),
				new TransformTrack(
					AnimationBinding.ForProperty("Door", string.Empty),
					Vector3Curve.Empty,
					QuaternionCurve.Empty,
					Vector3Curve.Empty)
			],
			PropertyTracks =
			[
				new PropertyTrack(
					AnimationBinding.ForProperty("Lamp", "Light.Intensity"),
					new FloatCurve([0.0f, 0.5f], [0.0f, 3.0f], CurveInterpolation.CubicHermite, [0.0f, 0.0f], [1.0f, 1.0f]))
			]
		};

		var path = Path.Combine(_directory, "walk.anim.bin");
		AnimationClipSerializer.Write(path, written);
		var read = AnimationClipSerializer.Read(path);

		Assert.Multiple(() =>
		{
			Assert.That(read.Name, Is.EqualTo("walk"));
			Assert.That(read.Duration, Is.EqualTo(1.25f).Within(1e-6f));
			Assert.That(read.FramesPerSecond, Is.EqualTo(24.0f).Within(1e-6f));
			Assert.That(read.SourceSkeletonName, Is.EqualTo("rig"));
			Assert.That(read.TransformTracks, Has.Length.EqualTo(2));
			Assert.That(read.TransformTracks[0].Binding.Kind, Is.EqualTo(AnimationBindingKind.Bone));
			Assert.That(read.TransformTracks[0].Binding.Path, Is.EqualTo("root"));
			Assert.That(read.TransformTracks[0].Position.Values[1], Is.EqualTo(Vector3.UnitX));
			Assert.That(read.TransformTracks[1].IsBoneTrack, Is.False);
			Assert.That(read.PropertyTracks, Has.Length.EqualTo(1));
			Assert.That(read.PropertyTracks[0].Binding.Property, Is.EqualTo("Light.Intensity"));
			Assert.That(read.PropertyTracks[0].Curve.Interpolation, Is.EqualTo(CurveInterpolation.CubicHermite));
			Assert.That(read.PropertyTracks[0].Curve.OutTangents, Is.EqualTo(new[] { 1.0f, 1.0f }));
			Assert.That(read.ToClip().NonBoneTransformTrackCount, Is.EqualTo(1));
		});
	}

	[Test]
	public void ImportedMeshSerializer_RoundTripsSkinInfluences()
	{
		var written = new ImportedMeshAssetFile
		{
			Vertices = [new Vector4(1, 2, 3, 1), new Vector4(4, 5, 6, 1)],
			Indices = [0, 1, 0],
			Normals = [Vector3.UnitY, Vector3.UnitY],
			Tangents = [new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1)],
			UVs = [Vector2.Zero, Vector2.One],
			BoneIndices = [0, 1, 2, 3, 4, 5, 6, 7],
			BoneWeights = [0.5f, 0.5f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, 0.0f]
		};

		var path = Path.Combine(_directory, "skinned.mesh.bin");
		ImportedMeshSerializer.Write(path, written);
		var read = ImportedMeshSerializer.Read(path);

		Assert.Multiple(() =>
		{
			Assert.That(read.Version, Is.EqualTo(ImportedMeshSerializer.CurrentVersion));
			Assert.That(read.BoneIndices, Is.EqualTo(written.BoneIndices));
			Assert.That(read.BoneWeights, Is.EqualTo(written.BoneWeights));
			Assert.That(read.Vertices, Is.EqualTo(written.Vertices));
		});
	}

	/// <summary>
	/// The Library is a build artifact, but a version bump that threw would force a full rebuild on
	/// everyone who pulls this change. Version 1 payloads have to keep loading as unskinned meshes.
	/// </summary>
	[Test]
	public void ImportedMeshSerializer_ReadsVersionOneArtifactsAsUnskinned()
	{
		var path = Path.Combine(_directory, "legacy.mesh.bin");
		using (var stream = File.Create(path))
		using (var writer = new BinaryWriter(stream))
		{
			writer.Write("WEMH"u8);
			writer.Write(1);
			WriteVector4Array(writer, [new Vector4(1, 2, 3, 1)]);
			writer.Write(1);
			writer.Write(0u);
			WriteVector3Array(writer, [Vector3.UnitY]);
			WriteVector4Array(writer, [new Vector4(1, 0, 0, 1)]);
			writer.Write(1);
			writer.Write(0.0f);
			writer.Write(0.0f);
		}

		var read = ImportedMeshSerializer.Read(path);

		Assert.Multiple(() =>
		{
			Assert.That(read.Version, Is.EqualTo(1));
			Assert.That(read.Vertices, Has.Length.EqualTo(1));
			Assert.That(read.BoneIndices, Is.Empty);
			Assert.That(read.BoneWeights, Is.Empty);
		});
	}

	private static void WriteVector3Array(BinaryWriter writer, Vector3[] values)
	{
		writer.Write(values.Length);
		foreach (var value in values)
		{
			writer.Write(value.X);
			writer.Write(value.Y);
			writer.Write(value.Z);
		}
	}

	private static void WriteVector4Array(BinaryWriter writer, Vector4[] values)
	{
		writer.Write(values.Length);
		foreach (var value in values)
		{
			writer.Write(value.X);
			writer.Write(value.Y);
			writer.Write(value.Z);
			writer.Write(value.W);
		}
	}
}
