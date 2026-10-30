using NSubstitute;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
using SixLabors.ImageSharp;

namespace WolfEngine.Editor.Tests;

public sealed class AssetBrowserAppearanceTests
{
	[Test]
	public void EveryAssetTypeHasALabelAndAShipped24PixelIcon()
	{
		foreach (var type in Enum.GetValues<AssetType>())
		{
			var appearance = AssetBrowserAppearance.For(type);
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Assets", "Icons", appearance.Icon + ".png");
			Assert.That(appearance.Label, Is.Not.Empty, type.ToString());
			Assert.That(File.Exists(path), Is.True, path);
			var info = Image.Identify(path);
			Assert.That(info.Size, Is.EqualTo(new Size(24, 24)), path);
		}
	}

	[TestCase("Assets/Hero.animgraph.json", "Hero")]
	[TestCase("Assets/Rogue.animset.data.json", "Rogue")]
	[TestCase("Assets/Tree.fbx", "Tree")]
	[TestCase("Assets/Roughness.Detail.png", "Roughness.Detail")]
	public void CardTitlesRemoveOnlyTheAssetSuffix(string path, string expected)
	{
		Assert.That(AssetBrowserAppearance.Title(path), Is.EqualTo(expected));
	}

	[Test]
	public void FolderSearchFindsASourceThroughItsSubAssetNamesWithoutDuplicates()
	{
		var model = Model();
		var root = model.FoldersByPath["Assets"];
		var matches = AssetsWindowBrowserModelBuilder.GetFolderContents(root, "sPrInT");
		Assert.That(matches.Sources, Has.Count.EqualTo(1));
		Assert.That(matches.Sources[0].PrimaryAsset.Type, Is.EqualTo(AssetType.Model3D));
	}

	[Test]
	public void SubAssetSearchAndTypeFilterAreCombinedWithoutChangingTheSource()
	{
		var source = Model().SourcesBySourceId.Values.Single();
		var count = source.SubAssets.Count;
		var clips = AssetBrowserAppearance.FilterSubAssets(source, "  sprint  ", AssetType.AnimationClip);
		Assert.That(clips.Select(asset => asset.Name), Is.EqualTo(new[] { "Sprint_Forward", "Sprint_Left" }));
		Assert.That(
			AssetBrowserAppearance.FilterSubAssets(source, " ", AssetType.Skeleton),
			Has.Count.EqualTo(1));
		Assert.That(AssetBrowserAppearance.FilterSubAssets(source, "missing", null), Is.Empty);
		Assert.That(source.SubAssets.Count, Is.EqualTo(count));
	}

	[Test]
	public void BrowserSelectionKeepsSearchLocationWhileExternalSubAssetSelectionRevealsItsParent()
	{
		var model = Model();
		var source = model.SourcesBySourceId.Values.Single();
		var project = Substitute.For<IEditorProjectService>();
		var assets = source.SubAssets.Append(source.PrimaryAsset).ToArray();
		project.TryGetAsset(Arg.Any<Guid>(), out Arg.Any<AssetDatabaseEntry>()).Returns(call =>
		{
			var asset = assets.FirstOrDefault(candidate => candidate.Id == call.ArgAt<Guid>(0));
			call[1] = asset;
			return asset is not null;
		});
		var selected = new AssetSelectionService();
		var state = new AssetsWindowSelectionState();
		var clip = source.SubAssets.First(asset => asset.Type == AssetType.AnimationClip);
		selected.Select(clip.Id, false);
		state.ObserveBrowserSelection(clip.Id);
		state.Prune(model, project, selected);
		Assert.That(state.SelectedFolderPath, Is.EqualTo("Assets"));
		var skeleton = source.SubAssets.Single(asset => asset.Type == AssetType.Skeleton);
		selected.Select(skeleton.Id, false);
		state.Prune(model, project, selected);
		Assert.That(state.SelectedFolderPath, Is.EqualTo("Assets/Models"));
		Assert.That(state.ExpandedSourceId, Is.EqualTo(source.SourceId));
	}

	private static AssetsWindowBrowserModel Model()
	{
		var sourceId = Guid.NewGuid();
		var entries = new List<AssetDatabaseEntry>();
		(AssetType Type, string Name)[] definitions =
		[
			(AssetType.Model3D, "Locomotion"),
			(AssetType.AnimationClip, "Sprint_Forward"),
			(AssetType.AnimationClip, "Sprint_Left"),
			(AssetType.Mesh, "Sprint_Mesh"),
			(AssetType.Skeleton, "RootNode")
		];
		foreach (var (type, name) in definitions)
		{
			entries.Add(new AssetDatabaseEntry
			{
				Id = Guid.NewGuid(),
				SourceId = sourceId,
				Type = type,
				Name = name,
				NodeKey = name,
				RelativeSourcePath = "Assets/Models/Locomotion.fbx",
				RelativeAssetPath = "Assets/Models/Locomotion.fbx"
			});
		}

		return AssetsWindowBrowserModelBuilder.Build(
			entries,
			Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Assets"));
	}
}
