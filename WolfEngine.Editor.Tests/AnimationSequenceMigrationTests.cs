using System.Text.Json;
using System.Text.Json.Nodes;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class AnimationSequenceMigrationTests
{
	private string _root = "";
	private readonly AssetMetadataStore _metadata = new();

	[SetUp]
	public void Setup()
	{
		_root = Path.Combine(Path.GetTempPath(), "sequence-migration-" + Guid.NewGuid());
		Directory.CreateDirectory(Path.Combine(_root, "Assets"));
	}

	[TearDown]
	public void Cleanup()
	{
		Directory.Delete(_root, true);
	}

	[TestCase(1)]
	[TestCase(2)]
	public void UpgradeProject_InlinesSharedSequences_PreservesSetIdsAndSpeed(int version)
	{
		var clipId = Guid.NewGuid();
		var skeletonId = Guid.NewGuid();
		var sequenceId = WriteSequence("Run", clipId, 1.4f, new(), version);
		var firstSet = WriteSet("Hero", sequenceId, skeletonId, version);
		var secondSet = WriteSet("Enemy", sequenceId, skeletonId, version);

		Assert.That(AnimationSequenceMigration.UpgradeProject(_root), Is.EqualTo(3));

		foreach (var (path, id) in new[] { firstSet, secondSet })
		{
			var set = (AnimationSet)new DataAssetStore().LoadAsset(path).Asset;
			Assert.Multiple(() =>
			{
				Assert.That(set.Version, Is.EqualTo(AnimationSet.CurrentVersion));
				Assert.That(set.Skeleton.NodeId, Is.EqualTo(skeletonId));
				Assert.That(set.Clips["Run"].Clip.NodeId, Is.EqualTo(clipId));
				Assert.That(set.Clips["Run"].PlaybackSpeed, Is.EqualTo(1.4f));
				Assert.That(_metadata.Load(path + ".meta").SubAssets.Single().NodeId, Is.EqualTo(id));
			});
		}

		Assert.That(File.Exists(AssetPath("Run.data.json")), Is.False);
		Assert.That(File.Exists(AssetPath("Run.data.json.meta")), Is.False);
		Assert.That(AnimationSequenceMigration.UpgradeProject(_root), Is.Zero);
	}

	[Test]
	public void UpgradeProject_MovesAnnotationsIntoImportedTakeMetadata()
	{
		var clipId = Guid.NewGuid();
		var sourceId = Guid.NewGuid();
		var timeline = Timeline("Footstep");
		var sequenceId = WriteSequence("Run", clipId, 2, timeline);
		WriteSet("Hero", sequenceId, Guid.NewGuid());
		var sourcePath = AssetPath("Take.fbx");
		File.WriteAllText(sourcePath, "model");
		var metadata = new AssetSourceMetaFile
		{
			SourceId = sourceId,
			ImporterId = AssetImporterIds.ThreeDScene,
			SubAssets = [new() { Key = "animation:3", NodeId = clipId, Type = AssetType.AnimationClip }]
		};
		metadata.SetImportSettings(new ModelImportSettings { AnimationSkeletonId = Guid.NewGuid(), ScaleFactor = .01f });
		_metadata.Save(sourcePath + ".meta", metadata);

		AnimationSequenceMigration.UpgradeProject(_root);

		var result = _metadata.Load(sourcePath + ".meta");
		var settings = result.GetImportSettingsOrDefault(() => new ModelImportSettings());
		Assert.Multiple(() =>
		{
			Assert.That(result.SourceId, Is.EqualTo(sourceId));
			Assert.That(result.SubAssets.Single().NodeId, Is.EqualTo(clipId));
			Assert.That(settings.ScaleFactor, Is.EqualTo(.01f));
			Assert.That(settings.AnimationClips["animation:3"].Markers.Single().Name, Is.EqualTo("Footstep"));
			Assert.That(settings.AnimationClips["animation:3"].Curves.Single().OutTangents, Is.EqualTo(new[] { 1f, 2f }));
		});
		Assert.That(AnimationSequenceMigration.UpgradeProject(_root), Is.Zero);
	}

	[Test]
	public void UpgradeProject_SharedTakeWithDifferentTimelines_CreatesClipSubAssetsAndRewiresSlots()
	{
		var clipId = Guid.NewGuid();
		var actionId = WriteSequence("Action", clipId, 2, Timeline("Impact"));
		var idleId = WriteSequence("Idle", clipId, 1, new());
		var (actionPath, _) = WriteSet("ActionSet", actionId, Guid.NewGuid());
		var (idlePath, _) = WriteSet("IdleSet", idleId, Guid.NewGuid());
		var sourcePath = AssetPath("Take.fbx");
		File.WriteAllText(sourcePath, "model");
		_metadata.Save(sourcePath + ".meta", new AssetSourceMetaFile
		{
			SourceId = Guid.NewGuid(),
			ImporterId = AssetImporterIds.ThreeDScene,
			SubAssets = [new() { Key = "animation:0", NodeId = clipId, Type = AssetType.AnimationClip }]
		});

		AnimationSequenceMigration.UpgradeProject(_root);

		var metadata = _metadata.Load(sourcePath + ".meta");
		var settings = metadata.GetImportSettingsOrDefault(() => new ModelImportSettings());
		var variant = settings.AnimationClipVariants.Single();
		var action = (AnimationSet)new DataAssetStore().LoadAsset(actionPath).Asset;
		var idle = (AnimationSet)new DataAssetStore().LoadAsset(idlePath).Asset;
		Assert.Multiple(() =>
		{
			Assert.That(idle.Clips["Run"].Clip.NodeId, Is.EqualTo(clipId));
			Assert.That(action.Clips["Run"].Clip.NodeId, Is.Not.EqualTo(actionId));
			Assert.That(action.Clips["Run"].PlaybackSpeed, Is.EqualTo(2));
			Assert.That(variant.Value.SourceClipKey, Is.EqualTo("animation:0"));
			Assert.That(metadata.SubAssets.Single(entry => entry.Key == variant.Key).NodeId, Is.EqualTo(action.Clips["Run"].Clip.NodeId));
			Assert.That(settings.AnimationClips["animation:0"].Markers, Is.Empty);
			Assert.That(settings.AnimationClips[variant.Key].Markers.Single().Name, Is.EqualTo("Impact"));
		});
		Assert.That(AnimationSequenceMigration.UpgradeProject(_root), Is.Zero);
	}

	[Test]
	public void UpgradeProject_MovesAnnotationsIntoAuthoredClip()
	{
		var clipPath = AssetPath("Run.anim.json");
		AuthoredAnimationClipJson.Write(clipPath, new AuthoredAnimationClipFile());
		var clipId = Guid.NewGuid();
		WriteMetadata(clipPath, clipId);
		WriteSequence("Run", clipId, 1, Timeline("Step"));

		AnimationSequenceMigration.UpgradeProject(_root);

		var clip = AuthoredAnimationClipJson.Read(clipPath);
		Assert.That(clip.Markers.Single().Name, Is.EqualTo("Step"));
		Assert.That(clip.Compile("Run", "", []).ToClip().Curves.Single().Name, Is.EqualTo("Weight"));
	}

	[Test]
	public void UpgradeProject_ConflictingAuthoredTimelines_LeavesEverySourceUntouched()
	{
		var clipId = Guid.NewGuid();
		var sourcePath = AssetPath("Source.anim.json");
		AuthoredAnimationClipJson.Write(sourcePath, new AuthoredAnimationClipFile());
		WriteMetadata(sourcePath, clipId);
		var sequenceId = WriteSequence("Run", clipId, 1, Timeline("Step"));
		WriteSequence("Other", clipId, 1, new());
		WriteSet("Hero", sequenceId, Guid.NewGuid());
		AssertFailureLeavesSourcesUntouched("different curves or markers");
	}

	[Test]
	public void UpgradeProject_MissingSequence_LeavesEverySourceUntouched()
	{
		WriteSet("Hero", Guid.NewGuid(), Guid.NewGuid());
		AssertFailureLeavesSourcesUntouched("is missing");
	}

	[Test]
	public void UpgradeProject_MissingAnnotatedClip_LeavesEverySourceUntouched()
	{
		var sequenceId = WriteSequence("Run", Guid.NewGuid(), 1, Timeline("Step"));
		WriteSet("Hero", sequenceId, Guid.NewGuid());
		AssertFailureLeavesSourcesUntouched("source metadata");
	}

	[Test]
	public void UpgradeProject_StandaloneLegacyAssets_PreserveSetIdentity()
	{
		var clipId = Guid.NewGuid();
		var sequenceId = Guid.NewGuid();
		var setId = Guid.NewGuid();
		var sequencePath = AssetPath("Run.animclip.json");
		var setPath = AssetPath("Hero.animset.json");
		File.WriteAllText(sequencePath, $$$"""{"Version":1,"ClipId":"{{{clipId}}}","PlaybackSpeed":2}""");
		File.WriteAllText(setPath, $$$"""{"Version":1,"Clips":{"Run":"{{{sequenceId}}}"}}""");
		WriteMetadata(sequencePath, sequenceId);
		WriteMetadata(setPath, setId);

		AnimationLegacyMigration.UpgradeProject(_root);

		var destination = AssetPath("Hero.animset.data.json");
		var set = (AnimationSet)new DataAssetStore().LoadAsset(destination).Asset;
		Assert.Multiple(() =>
		{
			Assert.That(set.Clips["Run"].Clip.NodeId, Is.EqualTo(clipId));
			Assert.That(set.Clips["Run"].PlaybackSpeed, Is.EqualTo(2));
			Assert.That(_metadata.Load(destination + ".meta").SubAssets.Single().NodeId, Is.EqualTo(setId));
			Assert.That(File.Exists(sequencePath), Is.False);
			Assert.That(File.Exists(setPath), Is.False);
		});
		Assert.That(AnimationLegacyMigration.UpgradeProject(_root), Is.Zero);
	}

	private void AssertFailureLeavesSourcesUntouched(string message)
	{
		var before = Directory.GetFiles(Path.Combine(_root, "Assets")).ToDictionary(path => path, File.ReadAllText);

		Assert.That(() => AnimationSequenceMigration.UpgradeProject(_root), Throws.TypeOf<InvalidDataException>().With.Message.Contains(message));

		Assert.That(Directory.GetFiles(Path.Combine(_root, "Assets")), Is.EquivalentTo(before.Keys));
		foreach (var source in before)
		{
			Assert.That(File.ReadAllText(source.Key), Is.EqualTo(source.Value));
		}
	}

	private Guid WriteSequence(string name, Guid clipId, float speed, AnimationClipMetadata timeline, int version = 2)
	{
		var data = JsonSerializer.SerializeToNode(timeline, AnimationAssetJson.Options)!.AsObject();
		data["Version"] = version;
		data["PlaybackSpeed"] = speed;
		data[version == 1 ? "ClipId" : "Clip"] = version == 1
			? JsonValue.Create(clipId)
			: new JsonObject { ["NodeId"] = clipId };
		var path = AssetPath(name + ".data.json");
		WriteData(path, "WolfEngine.Animation.AnimationSequence, WolfEngine", data);
		var id = Guid.NewGuid();
		WriteMetadata(path, id);

		return id;
	}

	private (string Path, Guid Id) WriteSet(string name, Guid sequenceId, Guid skeletonId, int version = 2)
	{
		var data = new JsonObject { ["Version"] = version };
		data[version == 1 ? "SkeletonId" : "Skeleton"] = version == 1
			? JsonValue.Create(skeletonId)
			: new JsonObject { ["NodeId"] = skeletonId };
		data["Clips"] = new JsonObject
		{
			["Run"] = version == 1 ? JsonValue.Create(sequenceId) : new JsonObject { ["NodeId"] = sequenceId }
		};
		var path = AssetPath(name + ".data.json");
		WriteData(path, typeof(AnimationSet).AssemblyQualifiedName!, data);
		var id = Guid.NewGuid();
		WriteMetadata(path, id);

		return (path, id);
	}

	private static void WriteData(string path, string type, JsonObject data)
	{
		var file = new JsonObject
		{
			["Version"] = DataAssetFile.CurrentVersion,
			["AssetType"] = "DataAsset",
			["DataAssetType"] = type,
			["Data"] = data
		};
		File.WriteAllText(path, file.ToJsonString());
	}

	private void WriteMetadata(string path, Guid nodeId)
	{
		_metadata.Save(path + ".meta", new AssetSourceMetaFile
		{
			SourceId = Guid.NewGuid(),
			SubAssets = [new() { Key = "main", NodeId = nodeId, Type = AssetType.DataAsset }]
		});
	}

	private string AssetPath(string name) => Path.Combine(_root, "Assets", name);

	private static AnimationClipMetadata Timeline(string marker) => new()
	{
		Markers = [new() { Name = marker, Time = .5f }],
		Curves = [new() { Name = "Weight", Times = [0, 1], Values = [0, 1], Interpolation = CurveInterpolation.CubicHermite, InTangents = [0, 0], OutTangents = [1, 2] }]
	};
}
