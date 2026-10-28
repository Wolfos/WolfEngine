using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class AnimationAssetRefMigrationTests
{
	private string _root = "";
	private string _assets = "";

	[SetUp]
	public void Setup()
	{
		_root = Path.Combine(Path.GetTempPath(), "animation-assetref-migration-" + Guid.NewGuid());
		_assets = Path.Combine(_root, "Assets");
		Directory.CreateDirectory(_assets);
	}

	[TearDown]
	public void Cleanup()
	{
		Directory.Delete(_root, true);
	}

	[Test]
	public void UpgradeProject_ConvertsGuidReferencesToAssetRefs()
	{
		var skeletonId = Guid.NewGuid();
		var sequenceId = Guid.NewGuid();
		var clipId = Guid.NewGuid();
		var maskId = Guid.NewGuid();
		var setPath = WriteDataAsset("Hero.data.json", typeof(AnimationSet),
			$$$"""{"Version":1,"SkeletonId":"{{{skeletonId}}}","Clips":{"Run":"{{{sequenceId}}}"}}""");
		var sequencePath = WriteDataAsset("Run.data.json", "WolfEngine.Animation.AnimationSequence, WolfEngine",
			$$$"""{"Version":1,"ClipId":"{{{clipId}}}","PlaybackSpeed":2,"Curves":[],"Markers":[]}""");
		var maskPath = WriteDataAsset("UpperBody.data.json", typeof(BoneMask),
			$$$"""{"Version":1,"SkeletonId":"{{{skeletonId}}}","Bones":[{"Bone":"spine","Weight":1,"IncludeChildren":true}]}""");
		var graphPath = Path.Combine(_assets, "Hero" + AnimationGraph.Extension);
		File.WriteAllText(
			graphPath,
			$$$"""{"Version":1,"Parameters":[],"Nodes":[{"Kind":"MaskedBlend","MaskId":"{{{maskId}}}"}],"Layout":[]}""");

		new AssetMetadataStore().Save(sequencePath + ".meta", new AssetSourceMetaFile
		{
			SourceId = Guid.NewGuid(),
			SubAssets = [new() { Key = "main", NodeId = sequenceId, Type = AssetType.DataAsset }]
		});

		Assert.That(AnimationSequenceMigration.UpgradeProject(_root), Is.EqualTo(2));
		Assert.That(AnimationAssetRefMigration.UpgradeProject(_root), Is.EqualTo(2));

		var store = new DataAssetStore();
		var set = (AnimationSet)store.LoadAsset(setPath).Asset;
		var mask = (BoneMask)store.LoadAsset(maskPath).Asset;
		var graph = AnimationAssetJson.Read<AnimationGraph>(graphPath);
		Assert.Multiple(() =>
		{
			Assert.That(set.Version, Is.EqualTo(AnimationSet.CurrentVersion));
			Assert.That(set.Skeleton.NodeId, Is.EqualTo(skeletonId));
			Assert.That(set.Clips["Run"].Clip.NodeId, Is.EqualTo(clipId));
			Assert.That(set.Clips["Run"].PlaybackSpeed, Is.EqualTo(2));
			Assert.That(File.Exists(sequencePath), Is.False);
			Assert.That(mask.Version, Is.EqualTo(BoneMask.CurrentVersion));
			Assert.That(mask.Skeleton.NodeId, Is.EqualTo(skeletonId));
			Assert.That(mask.Bones.Single().Bone, Is.EqualTo("spine"));
			Assert.That(graph.Version, Is.EqualTo(AnimationGraph.CurrentVersion));
			Assert.That(graph.Nodes.Single().Mask.NodeId, Is.EqualTo(maskId));
		});
	}

	[Test]
	public void UpgradeProject_IsIdempotentAndIgnoresOtherDataAssets()
	{
		var setPath = WriteDataAsset("Hero.data.json", typeof(AnimationSet),
			$$$"""{"Version":1,"SkeletonId":"{{{Guid.NewGuid()}}}","Clips":{}}""");
		var otherPath = Path.Combine(_assets, "Other.data.json");
		const string otherJson = """{"Version":1,"AssetType":"DataAsset","DataAssetType":"Game.Config, Game","DataAssetTypeId":"","Data":{"Version":1,"SkeletonId":"x"}}""";
		File.WriteAllText(otherPath, otherJson);

		Assert.That(AnimationAssetRefMigration.UpgradeProject(_root), Is.EqualTo(1));

		var upgraded = File.ReadAllText(setPath);

		Assert.Multiple(() =>
		{
			Assert.That(AnimationAssetRefMigration.UpgradeProject(_root), Is.Zero);
			Assert.That(File.ReadAllText(setPath), Is.EqualTo(upgraded));
			Assert.That(File.ReadAllText(otherPath), Is.EqualTo(otherJson));
		});
	}

	private string WriteDataAsset(string fileName, Type dataAssetType, string dataJson) =>
		WriteDataAsset(fileName, dataAssetType.AssemblyQualifiedName!, dataJson);

	private string WriteDataAsset(string fileName, string typeName, string dataJson)
	{
		var path = Path.Combine(_assets, fileName);
		File.WriteAllText(
			path,
			$$$"""{"Version":{{{DataAssetFile.CurrentVersion}}},"AssetType":"DataAsset","DataAssetType":"{{{typeName}}}","DataAssetTypeId":"","Data":{{{dataJson}}}}""");

		return path;
	}
}
