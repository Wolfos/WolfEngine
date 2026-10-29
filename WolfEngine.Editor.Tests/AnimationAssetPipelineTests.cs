using System.Numerics;
using Microsoft.Data.Sqlite;
using NSubstitute;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Importing;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class AnimationAssetPipelineTests
{
	private string _root = "";

	[SetUp]
	public void Setup()
	{
		_root = Path.Combine(Path.GetTempPath(), "animation-pipeline-" + Guid.NewGuid());
	}

	[TearDown]
	public void Cleanup()
	{
		SqliteConnection.ClearAllPools();
		Directory.Delete(_root, true);
	}

	[Test]
	public void RigReimport_RebuildsClipsAndSets_PreservesTimelineMetadataAndIds()
	{
		var importer = Substitute.For<IThreeDFileImporter>();
		var rig = new ImportedSkeleton("rig", ["root"], [-1], [BoneTransform.Identity], [Matrix4x4.Identity]);
		importer.Import(Arg.Is<string>(path => path.EndsWith("Rig.FBX")), Arg.Any<ModelImportSettings>())
			.Returns(new ImportedScene("rig", [], [], [new("root", Matrix4x4.Identity, [], -1)], [rig], []));
		importer.Import(Arg.Is<string>(path => path.EndsWith("Take.FBX")), Arg.Any<ModelImportSettings>())
			.Returns(new ImportedScene("take", [], [], [new("root", Matrix4x4.Identity, [], -1)], [],
				[new("take", 1, 30, -1, [new(AnimationBinding.ForProperty("root", ""), Vector3Curve.Empty, QuaternionCurve.Empty, Vector3Curve.Empty)], [])]));

		var index = new AssetPipelineIndex();
		var metadataStore = new AssetMetadataStore();
		var pipeline = new ProjectAssetPipelineService(index, metadataStore, Substitute.For<IImageLoader>(),
			new DataAssetStore(), new MaterialAssetStore(), importer, null!);
		pipeline.InitializeProject(_root);
		File.WriteAllText(Path.Combine(_root, "Assets/Rig.FBX"), "model");
		pipeline.ReimportSource(_root, "Assets/Rig.FBX");
		var rigId = metadataStore.Load(Path.Combine(_root, "Assets/Rig.FBX.meta"))
			.SubAssets.Single(asset => asset.Type == AssetType.Skeleton).NodeId;
		File.WriteAllText(Path.Combine(_root, "Assets/Take.FBX"), "clip");

		var settings = new ModelImportSettings
		{
			AnimationSkeletonId = rigId,
			AnimationClipVariants = new()
			{
				["animation:0:idle"] = new() { SourceClipKey = "animation:0", Name = "Idle" }
			},
			AnimationClips = new()
			{
				["animation:0"] = new()
				{
					Markers = [new() { Name = "Release", Time = .5f }],
					Curves = [new() { Name = "Intensity", Times = [0, 1], Values = [0, 1] }]
				}
			}
		};
		var metadata = new AssetSourceMetaFile { SourceId = Guid.NewGuid(), ImporterId = AssetImporterIds.ThreeDScene };
		metadata.SetImportSettings(settings);
		var takeMetadataPath = Path.Combine(_root, "Assets/Take.FBX.meta");
		metadataStore.Save(takeMetadataPath, metadata);
		pipeline.ReimportSource(_root, "Assets/Take.FBX");
		var clipId = metadataStore.Load(takeMetadataPath).SubAssets.Single(asset => asset.Key == "animation:0").NodeId;

		var idleId = metadataStore.Load(takeMetadataPath).SubAssets.Single(asset => asset.Key == "animation:0:idle").NodeId;

		var setPath = Path.Combine(_root, "Assets/Archer.data.json");
		new DataAssetStore().SaveAsset(setPath, typeof(AnimationSet), new AnimationSet
		{
			Skeleton = new() { NodeId = rigId },
			Clips = new()
			{
				["Action"] = new() { Clip = new() { NodeId = clipId }, PlaybackSpeed = 2 },
				["Idle"] = new() { Clip = new() { NodeId = idleId } }
			}
		});
		pipeline.ReimportSource(_root, "Assets/Archer.data.json");
		var setId = metadataStore.Load(setPath + ".meta").SubAssets.Single().NodeId;
		var before = File.ReadAllText(setPath);
		importer.ClearReceivedCalls();

		pipeline.ReimportSource(_root, "Assets/Rig.FBX");

		importer.Received().Import(Arg.Is<string>(path => path.EndsWith("Take.FBX")), Arg.Any<ModelImportSettings>());
		Assert.That(pipeline.TryGetAsset(_root, clipId, out var clipEntry), Is.True);
		var clip = AnimationClipSerializer.Read(Path.Combine(_root, clipEntry.RelativeAssetPath)).ToClip();
		Assert.That(pipeline.TryGetAsset(_root, idleId, out var idleEntry), Is.True);
		var idle = AnimationClipSerializer.Read(Path.Combine(_root, idleEntry.RelativeAssetPath)).ToClip();
		Assert.Multiple(() =>
		{
			Assert.That(File.ReadAllText(setPath), Is.EqualTo(before));
			Assert.That(metadataStore.Load(takeMetadataPath).SubAssets.Single(asset => asset.Key == "animation:0").NodeId, Is.EqualTo(clipId));
			Assert.That(pipeline.ExpandInvalidationClosure(_root, [rigId]), Does.Contain(clipId).And.Contain(setId));
			Assert.That(index.GetDependencies(_root).Any(dependency => dependency.FromNodeId == setId && dependency.ToNodeId == clipId), Is.True);
			Assert.That(metadataStore.Load(takeMetadataPath).SubAssets.Single(asset => asset.Key == "animation:0:idle").NodeId, Is.EqualTo(idleId));
			Assert.That(idle.Markers, Is.Empty);
			Assert.That(idle.TransformTracks.Length, Is.EqualTo(clip.TransformTracks.Length));
			Assert.That(idle.Name, Is.EqualTo("Idle"));
			Assert.That(pipeline.ExpandInvalidationClosure(_root, [rigId]), Does.Contain(idleId));
			Assert.That(clip.Markers.Single().Name, Is.EqualTo("Release"));
			Assert.That(clip.Curves.Single().Values, Is.EqualTo(new[] { 0f, 1f }));
			Assert.That(metadataStore.Load(takeMetadataPath).GetImportSettingsOrDefault(() => new ModelImportSettings())
				.AnimationClips["animation:0"].Markers.Single().Time, Is.EqualTo(.5f));
		});
	}
}
