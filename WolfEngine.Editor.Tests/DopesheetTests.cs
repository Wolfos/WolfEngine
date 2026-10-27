using System.Numerics;
using NSubstitute;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class DopesheetTests
{
	// hips -> spine -> arm -> hand, and hips -> leg.
	private static readonly Skeleton Rig = new(
		"rig",
		["hips", "spine", "arm", "hand", "leg"],
		[-1, 0, 1, 2, 0],
		Enumerable.Repeat(BoneTransform.Identity, 5).ToArray(),
		Enumerable.Repeat(Matrix4x4.Identity, 5).ToArray());

	private static readonly Dictionary<string, int[]> Keys = new(StringComparer.Ordinal)
	{
		["arm"] = [0, 10],
		["hand"] = [10, 20]
	};

	[Test]
	public void Rows_StartWithTheSummaryThenFollowTheHierarchy()
	{
		var model = Build(new HashSet<string>(), keyedOnly: false);

		Assert.That(model.Rows.Select(row => row.Name), Is.EqualTo(new[] { "Summary", "hips", "spine", "arm", "hand", "leg" }));
		Assert.That(model.Rows.Select(row => row.Depth), Is.EqualTo(new[] { 0, 0, 1, 2, 3, 1 }));
	}

	[Test]
	public void SummaryRow_ShowsEveryKeyedFrame()
	{
		var model = Build(new HashSet<string>(), keyedOnly: false);

		Assert.That(model.GetRowFrames(0), Is.EqualTo(new[] { 0, 10, 20 }));
		Assert.That(model.GetRowKeysAt(0, 10), Is.EquivalentTo(new[] { new AnimationKeyRef("arm", 10), new AnimationKeyRef("hand", 10) }));
	}

	[Test]
	public void KeyedOnly_KeepsAncestorsOfKeyedBones()
	{
		var model = Build(new HashSet<string>(), keyedOnly: true);

		Assert.That(model.Rows.Select(row => row.Name), Is.EqualTo(new[] { "Summary", "hips", "spine", "arm", "hand" }));
	}

	[Test]
	public void CollapsedRow_StandsForItsWholeSubtree()
	{
		var model = Build(new HashSet<string> { "spine" }, keyedOnly: false);

		Assert.That(model.Rows.Select(row => row.Name), Is.EqualTo(new[] { "Summary", "hips", "spine", "leg" }));
		var spine = model.Rows.ToList().FindIndex(row => row.Name == "spine");
		Assert.That(model.Rows[spine].IsExpanded, Is.False);
		Assert.That(model.GetRowFrames(spine), Is.EqualTo(new[] { 0, 10, 20 }));
		Assert.That(model.GetRowKeysAt(spine, 10), Has.Count.EqualTo(2));
	}

	[Test]
	public void CollapsingALeafHasNoEffect()
	{
		var model = Build(new HashSet<string> { "hand" }, keyedOnly: false);

		var hand = model.Rows.Single(row => row.Name == "hand");
		Assert.That(hand.IsExpanded, Is.True);
		Assert.That(hand.HasChildren, Is.False);
	}

	[Test]
	public void AggregateDiamond_IsSelectedOnlyWhenAllItsKeysAre()
	{
		var model = Build(new HashSet<string>(), keyedOnly: false);
		var selection = new HashSet<AnimationKeyRef> { new("arm", 10) };

		Assert.That(model.IsRowKeySelected(0, 10, selection), Is.False);

		selection.Add(new AnimationKeyRef("hand", 10));

		Assert.That(model.IsRowKeySelected(0, 10, selection), Is.True);
	}

	[Test]
	public void BoxSelection_CollectsKeysUnderTheBox()
	{
		var model = Build(new HashSet<string>(), keyedOnly: false);
		var arm = model.Rows.ToList().FindIndex(row => row.Name == "arm");
		var hand = model.Rows.ToList().FindIndex(row => row.Name == "hand");

		var boxed = model.GetKeysInBox(arm, hand, 5, 15);

		Assert.That(boxed, Is.EquivalentTo(new[] { new AnimationKeyRef("arm", 10), new AnimationKeyRef("hand", 10) }));
	}

	[Test]
	public void View_FitShowsTheWholeClipWithinTheMargins()
	{
		var view = DopesheetView.Fit(100.0f, 520.0f, 50, 10.0f);

		Assert.That(view.FrameToX(0), Is.EqualTo(110.0f).Within(1e-3f));
		Assert.That(view.FrameToX(50), Is.EqualTo(610.0f).Within(1e-3f));
		Assert.That(view.XToNearestFrame(360.0f), Is.EqualTo(25));
	}

	[Test]
	public void View_ZoomKeepsTheFrameUnderThePointer()
	{
		var view = DopesheetView.Fit(0.0f, 500.0f, 100, 0.0f);
		var before = view.XToFrame(200.0f);

		var zoomed = view.ZoomAround(200.0f, 2.0f, 1.0f, 80.0f);

		Assert.That(zoomed.XToFrame(200.0f), Is.EqualTo(before).Within(1e-3f));
		Assert.That(zoomed.PixelsPerFrame, Is.EqualTo(view.PixelsPerFrame * 2.0f).Within(1e-3f));
	}

	[Test]
	public void FocusedDocument_TakesDeleteAndDuplicate()
	{
		var delete = new EditorShortcutSnapshot(false, false, false, false, false, false, false, false, true, false, false, false);
		var duplicate = new EditorShortcutSnapshot(true, false, false, false, false, false, false, true, false, false, false, false);
		var shiftD = new EditorShortcutSnapshot(false, true, false, false, false, false, false, true, false, false, false, false);

		Assert.That(EditorShortcutCommandResolver.Resolve(delete, EditorFocusedWindow.Document), Is.EqualTo(EditorShortcutCommand.DeleteFocusedSelection));
		Assert.That(EditorShortcutCommandResolver.Resolve(duplicate, EditorFocusedWindow.Document), Is.EqualTo(EditorShortcutCommand.DuplicateFocusedSelection));
		Assert.That(EditorShortcutCommandResolver.Resolve(shiftD, EditorFocusedWindow.Document), Is.EqualTo(EditorShortcutCommand.None));
	}

	private static DopesheetModel Build(HashSet<string> collapsed, bool keyedOnly)
	{
		var model = new DopesheetModel();
		model.Rebuild(Rig, Keys, collapsed, keyedOnly);
		return model;
	}
}

[TestFixture]
public sealed class AnimationClipKeySelectionTests
{
	private static readonly BoneTransform Pose = BoneTransform.Identity;

	private string _root = string.Empty;
	private AnimationClipEditorSession _session = null!;

	[SetUp]
	public void SetUp()
	{
		_root = Path.Combine(Path.GetTempPath(), $"wolf-key-selection-{Guid.NewGuid():N}");
		Directory.CreateDirectory(Path.Combine(_root, "Assets"));

		var skeleton = new Skeleton("rig", ["arm", "leg"], [-1, -1], [Pose, Pose], [Matrix4x4.Identity, Matrix4x4.Identity]);
		var rigSource = Guid.NewGuid();
		var skeletonAsset = Asset(AssetType.Skeleton, rigSource, "Assets/Rig.fbx");
		var modelAsset = Asset(AssetType.Model3D, rigSource, "Assets/Rig.fbx");
		var clipAsset = Asset(AssetType.AnimationClip, Guid.NewGuid(), "Assets/Clip.anim.json");
		AuthoredAnimationClipJson.Write(Path.Combine(_root, clipAsset.RelativeSourcePath), new AuthoredAnimationClipFile
		{
			Skeleton = new AssetRef<Skeleton>
			{
				NodeId = skeletonAsset.Id
			}
		});

		var registry = Substitute.For<IAssetInstanceRegistry>();
		registry.GetInstance(skeletonAsset.Id, typeof(Skeleton)).Returns(skeleton);
		AssetDatabase.SetInstanceRegistry(registry);

		var project = Substitute.For<IEditorProjectService>();
		project.ProjectRootPath.Returns(_root);
		project.CurrentAssetDatabase.Returns(new AssetDatabase
		{
			Assets = [skeletonAsset, modelAsset, clipAsset]
		});
		project.GetAbsoluteAssetPath(Arg.Any<Guid>(), Arg.Any<string>())
			.Returns(call => Path.Combine(_root, call.ArgAt<string>(1)));

		var pipeline = Substitute.For<IProjectAssetPipelineService>();
		pipeline.GetDependencies(Arg.Any<string>()).Returns(Array.Empty<AssetDependencyRecord>());
		var workspaces = Substitute.For<IEditorWorkspaceService>();
		workspaces.Workspaces.Returns(Array.Empty<EditorWorkspace>());

		_session = new AnimationClipEditorSession(
			project,
			pipeline,
			workspaces,
			Substitute.For<IAssetSelectionService>(),
			null!,
			null!,
			null!);
		_session.Open(clipAsset);

		var document = _session.Document!;
		document.SetKey("arm", 0, Pose);
		document.SetKey("arm", 10, Pose);
		document.SetKey("leg", 10, Pose);
	}

	[TearDown]
	public void TearDown()
	{
		_session.Dispose();
		AssetDatabase.ClearInstanceRegistry();
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	[Test]
	public void BoneKeyFrames_FollowTheDocument()
	{
		var first = _session.GetBoneKeyFrames();

		Assert.That(first["arm"], Is.EqualTo(new[] { 0, 10 }));
		Assert.That(_session.GetBoneKeyFrames(), Is.SameAs(first));

		_session.Document!.SetKey("leg", 20, Pose);

		Assert.That(_session.GetBoneKeyFrames(), Is.Not.SameAs(first));
		Assert.That(_session.GetBoneKeyFrames()["leg"], Is.EqualTo(new[] { 10, 20 }));
	}

	[Test]
	public void Toggle_RemovesOnlyWhenEveryKeyWasSelected()
	{
		AnimationKeyRef[] column = [new("arm", 10), new("leg", 10)];
		_session.SelectKeys([column[0]], KeySelectionMode.Replace);

		_session.SelectKeys(column, KeySelectionMode.Toggle);

		Assert.That(_session.SelectedKeys, Is.EquivalentTo(column));

		_session.SelectKeys(column, KeySelectionMode.Toggle);

		Assert.That(_session.SelectedKeys, Is.Empty);
	}

	[Test]
	public void Move_ShiftsKeysAndTheSelection()
	{
		_session.SelectKeys([new AnimationKeyRef("arm", 10)], KeySelectionMode.Replace);

		Assert.That(_session.MoveSelectedKeys(5), Is.True);

		Assert.That(_session.Document!.GetKeyFrames("arm"), Is.EqualTo(new[] { 0, 15 }));
		Assert.That(_session.SelectedKeys, Is.EquivalentTo(new[] { new AnimationKeyRef("arm", 15) }));
	}

	[Test]
	public void Duplicate_SelectsTheCopies()
	{
		_session.SelectKeys([new AnimationKeyRef("arm", 0), new AnimationKeyRef("arm", 10)], KeySelectionMode.Replace);

		_session.DuplicateSelectedKeys(15);

		Assert.That(_session.Document!.GetKeyFrames("arm"), Is.EqualTo(new[] { 0, 10, 15, 25 }));
		Assert.That(_session.SelectedKeys, Is.EquivalentTo(new[] { new AnimationKeyRef("arm", 15), new AnimationKeyRef("arm", 25) }));
	}

	[Test]
	public void DuplicateToPlayhead_PlacesTheFirstKeyOnThePlayhead()
	{
		_session.SelectKeys([new AnimationKeyRef("arm", 0), new AnimationKeyRef("arm", 10)], KeySelectionMode.Replace);
		_session.SetPlayhead(18);

		_session.DuplicateSelectedKeysToPlayhead();

		Assert.That(_session.Document!.GetKeyFrames("arm"), Is.EqualTo(new[] { 0, 10, 18, 28 }));
	}

	[Test]
	public void DuplicateToPlayhead_OnTheFirstKeyGoesStraightAfterTheSelection()
	{
		_session.SelectKeys([new AnimationKeyRef("arm", 0), new AnimationKeyRef("arm", 10)], KeySelectionMode.Replace);

		_session.DuplicateSelectedKeysToPlayhead();

		Assert.That(_session.Document!.GetKeyFrames("arm"), Is.EqualTo(new[] { 0, 10, 11, 21 }));
	}

	[Test]
	public void ClampSelectionOffset_KeepsKeysInsideTheClip()
	{
		_session.SelectKeys([new AnimationKeyRef("arm", 0), new AnimationKeyRef("arm", 10)], KeySelectionMode.Replace);

		Assert.That(_session.ClampSelectionOffset(-5), Is.Zero);
		Assert.That(_session.ClampSelectionOffset(50), Is.EqualTo(20));
	}

	[Test]
	public void Delete_RemovesKeysAndClearsTheSelection()
	{
		_session.SelectKeys([new AnimationKeyRef("arm", 10), new AnimationKeyRef("leg", 10)], KeySelectionMode.Replace);

		Assert.That(_session.DeleteSelection(), Is.True);

		Assert.That(_session.GetBoneKeyFrames().ContainsKey("leg"), Is.False);
		Assert.That(_session.SelectedKeys, Is.Empty);
	}

	[Test]
	public void Undo_DropsSelectedKeysThatNoLongerExist()
	{
		_session.SelectKeys([new AnimationKeyRef("arm", 10)], KeySelectionMode.Replace);
		_session.DuplicateSelectedKeys(5);

		_session.Undo();

		Assert.That(_session.SelectedKeys, Is.Empty);
	}

	private static AssetDatabaseEntry Asset(AssetType type, Guid sourceId, string relativeSourcePath) =>
		new()
		{
			Id = Guid.NewGuid(),
			SourceId = sourceId,
			Type = type,
			Name = Path.GetFileName(relativeSourcePath),
			RelativeSourcePath = relativeSourcePath
		};
}
