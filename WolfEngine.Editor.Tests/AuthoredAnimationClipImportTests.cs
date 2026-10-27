using System.Numerics;
using Microsoft.Data.Sqlite;
using NSubstitute;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Importing;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class AuthoredAnimationClipImportTests
{
	private string _root = string.Empty;
	private AssetPipelineIndex _index = null!;
	private AssetMetadataStore _metadata = null!;
	private IThreeDFileImporter _modelImporter = null!;
	private ProjectAssetPipelineService _pipeline = null!;

	[SetUp]
	public void SetUp()
	{
		_root = Path.Combine(Path.GetTempPath(), $"wolf-authored-clip-import-{Guid.NewGuid():N}");
		Directory.CreateDirectory(Path.Combine(_root, "Assets"));

		_index = new AssetPipelineIndex();
		_metadata = new AssetMetadataStore();
		_modelImporter = Substitute.For<IThreeDFileImporter>();
		_pipeline = new ProjectAssetPipelineService(
			_index,
			_metadata,
			Substitute.For<IImageLoader>(),
			new DataAssetStore(),
			new MaterialAssetStore(),
			_modelImporter,
			null!);
		_pipeline.InitializeProject(_root);
	}

	[TearDown]
	public void TearDown()
	{
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	[Test]
	public void Import_ProducesARuntimeClipBoundToTheSkeleton()
	{
		var rigId = ImportRig();
		var clip = new AuthoredAnimationClipFile
		{
			Skeleton = new AssetRef<Skeleton>
			{
				NodeId = rigId
			},
			PreviewModelId = Guid.NewGuid(),
			FramesPerSecond = 24,
			LengthFrames = 48,
			Loop = false
		};
		var track = AuthoredTransformTrack.ForBone("arm");
		track.Rotation.Set(0, Quaternion.Identity);
		track.Rotation.Set(24, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.0f));
		clip.TransformTracks.Add(track);
		AuthoredAnimationClipJson.Write(Path.Combine(_root, "Assets/Swing.anim.json"), clip);

		_pipeline.ReimportSource(_root, "Assets/Swing.anim.json");

		var meta = _metadata.Load(Path.Combine(_root, "Assets/Swing.anim.json.meta"));
		Assert.That(meta.ImporterId, Is.EqualTo(AssetImporterIds.AnimationClip));
		var clipId = meta.SubAssets.Single().NodeId;
		Assert.That(meta.SubAssets.Single().Type, Is.EqualTo(AssetType.AnimationClip));

		Assert.That(_pipeline.TryGetAsset(_root, clipId, out var entry), Is.True);
		var runtime = AnimationClipSerializer.Read(Path.Combine(_root, entry.RelativeAssetPath)).ToClip();
		Assert.That(runtime.Name, Is.EqualTo("Swing"));
		Assert.That(runtime.Duration, Is.EqualTo(2.0f));
		Assert.That(runtime.Loop, Is.False);
		Assert.That(runtime.SourceSkeletonName, Is.EqualTo("rig"));
		Assert.That(runtime.SourceBindPoseLocal, Has.Length.EqualTo(2));
		Assert.That(runtime.TransformTracks.Single().Rotation.Times, Is.EqualTo(new[] { 0.0f, 1.0f }));

		var dependencies = _index.GetDependencies(_root);
		Assert.That(dependencies.Any(dependency => dependency.FromNodeId == clipId && dependency.ToNodeId == rigId), Is.True);
		Assert.That(dependencies.Any(dependency => dependency.FromNodeId == clipId && dependency.ToNodeId == clip.PreviewModelId), Is.False);
	}

	[Test]
	public void Reimport_KeepsTheClipId()
	{
		var path = Path.Combine(_root, "Assets/Idle.anim.json");
		AuthoredAnimationClipJson.Write(path, new AuthoredAnimationClipFile());
		_pipeline.ReimportSource(_root, "Assets/Idle.anim.json");
		var firstId = _metadata.Load(path + ".meta").SubAssets.Single().NodeId;

		var clip = AuthoredAnimationClipJson.Read(path);
		clip.LengthFrames = 60;
		AuthoredAnimationClipJson.Write(path, clip);
		_pipeline.ReimportSource(_root, "Assets/Idle.anim.json");

		Assert.That(_metadata.Load(path + ".meta").SubAssets.Single().NodeId, Is.EqualTo(firstId));
		Assert.That(_pipeline.TryGetAsset(_root, firstId, out var entry), Is.True);
		Assert.That(AnimationClipSerializer.Read(Path.Combine(_root, entry.RelativeAssetPath)).Duration, Is.EqualTo(2.0f));
	}

	[Test]
	public void Import_WithoutAnImportedSkeletonLeavesTheBindPoseEmpty()
	{
		var clip = new AuthoredAnimationClipFile
		{
			Skeleton = new AssetRef<Skeleton>
			{
				NodeId = Guid.NewGuid()
			}
		};
		AuthoredAnimationClipJson.Write(Path.Combine(_root, "Assets/Orphan.anim.json"), clip);

		_pipeline.ReimportSource(_root, "Assets/Orphan.anim.json");

		var clipId = _metadata.Load(Path.Combine(_root, "Assets/Orphan.anim.json.meta")).SubAssets.Single().NodeId;
		Assert.That(_pipeline.TryGetAsset(_root, clipId, out var entry), Is.True);
		var runtime = AnimationClipSerializer.Read(Path.Combine(_root, entry.RelativeAssetPath));
		Assert.That(runtime.SourceSkeletonName, Is.Empty);
		Assert.That(runtime.SourceBindPoseLocal, Is.Empty);
	}

	private Guid ImportRig()
	{
		var rig = new ImportedSkeleton(
			"rig",
			["root", "arm"],
			[-1, 0],
			[BoneTransform.Identity, BoneTransform.Identity],
			[Matrix4x4.Identity, Matrix4x4.Identity]);
		_modelImporter
			.Import(Arg.Is<string>(path => path.EndsWith("Rig.fbx")), Arg.Any<ModelImportSettings>())
			.Returns(new ImportedScene("rig", [], [], [new("root", Matrix4x4.Identity, [], -1)], [rig], []));

		File.WriteAllText(Path.Combine(_root, "Assets/Rig.fbx"), "model");
		_pipeline.ReimportSource(_root, "Assets/Rig.fbx");

		return _metadata.Load(Path.Combine(_root, "Assets/Rig.fbx.meta"))
			.SubAssets
			.Single(asset => asset.Type == AssetType.Skeleton)
			.NodeId;
	}
}
