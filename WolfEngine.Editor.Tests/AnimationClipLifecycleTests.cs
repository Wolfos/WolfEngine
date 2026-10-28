using System.Numerics;
using NSubstitute;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class AnimationClipLifecycleTests
{
	private static readonly BoneTransform Pose = BoneTransform.Identity;

	private string _root = string.Empty;
	private List<AssetDatabaseEntry> _assets = null!;
	private Skeleton _skeleton = null!;
	private Skeleton _otherSkeleton = null!;
	private AssetDatabaseEntry _skeletonAsset = null!;
	private AssetDatabaseEntry _modelAsset = null!;
	private AssetDatabaseEntry _otherSkeletonAsset = null!;
	private AssetDatabaseEntry _otherModelAsset = null!;
	private AssetDatabaseEntry _importedClipAsset = null!;
	private AnimationClip _importedClip = null!;
	private IEditorProjectService _project = null!;
	private IAssetSelectionService _selection = null!;
	private AnimationClipEditorSession _session = null!;

	[SetUp]
	public void SetUp()
	{
		_root = Path.Combine(Path.GetTempPath(), $"wolf-clip-lifecycle-{Guid.NewGuid():N}");
		Directory.CreateDirectory(Path.Combine(_root, "Assets", "Characters"));

		_skeleton = new Skeleton("rig", ["arm"], [-1], [Pose], [Matrix4x4.Identity]);
		_otherSkeleton = new Skeleton("cow", ["tail"], [-1], [Pose], [Matrix4x4.Identity]);
		var rigSource = Guid.NewGuid();
		var cowSource = Guid.NewGuid();
		_skeletonAsset = Asset(AssetType.Skeleton, rigSource, "rig", "Assets/Characters/Rig.fbx");
		_modelAsset = Asset(AssetType.Model3D, rigSource, "Rig.fbx", "Assets/Characters/Rig.fbx");
		_otherSkeletonAsset = Asset(AssetType.Skeleton, cowSource, "cow", "Assets/Cow.fbx");
		_otherModelAsset = Asset(AssetType.Model3D, cowSource, "Cow.fbx", "Assets/Cow.fbx");
		_importedClipAsset = Asset(AssetType.AnimationClip, rigSource, "Wave", "Assets/Characters/Rig.fbx");
		_importedClip = new AnimationClip(
			"Wave",
			1.0f,
			30.0f,
			true,
			[
				new TransformTrack(
					AnimationBinding.ForBone("arm"),
					Vector3Curve.Empty,
					new QuaternionCurve([0.0f, 0.5f], [Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.0f)]),
					Vector3Curve.Empty)
			],
			[],
			"rig",
			[]);
		File.WriteAllText(Path.Combine(_root, _importedClipAsset.RelativeSourcePath), "model");
		new AssetMetadataStore().Save(Path.Combine(_root, _importedClipAsset.RelativeSourcePath + ".meta"), new AssetSourceMetaFile
		{
			SourceId = rigSource,
			ImporterId = AssetImporterIds.ThreeDScene,
			SubAssets = [new() { Key = "animation:0", NodeId = _importedClipAsset.Id, Type = AssetType.AnimationClip }]
		});
		_assets = [_skeletonAsset, _modelAsset, _otherSkeletonAsset, _otherModelAsset, _importedClipAsset];

		var registry = Substitute.For<IAssetInstanceRegistry>();
		registry.GetInstance(_skeletonAsset.Id, typeof(Skeleton)).Returns(_skeleton);
		registry.GetInstance(_otherSkeletonAsset.Id, typeof(Skeleton)).Returns(_otherSkeleton);
		registry.GetInstance(_importedClipAsset.Id, typeof(AnimationClip)).Returns(_importedClip);
		AssetDatabase.SetInstanceRegistry(registry);

		var database = new AssetDatabase
		{
			Assets = _assets
		};
		_project = Substitute.For<IEditorProjectService>();
		_project.ProjectRootPath.Returns(_root);
		_project.HasOpenProject.Returns(true);
		_project.CurrentAssetDatabase.Returns(database);
		_project.GetAbsolutePath(Arg.Any<string>()).Returns(call => Path.Combine(_root, call.Arg<string>()));
		_project.GetAbsoluteAssetPath(Arg.Any<Guid>(), Arg.Any<string>())
			.Returns(call => Path.Combine(_root, call.ArgAt<string>(1)));
		_project.TryGetAsset(Arg.Any<Guid>(), out Arg.Any<AssetDatabaseEntry>())
			.Returns(call =>
			{
				var found = _assets.FirstOrDefault(asset => asset.Id == call.ArgAt<Guid>(0));
				call[1] = found!;
				return found is not null;
			});

		// Refreshing a new clip source indexes it, as the real pipeline does.
		_project.When(project => project.RefreshAssetSource(Arg.Any<string>()))
			.Do(call =>
			{
				var path = call.Arg<string>();
				if (_assets.Any(asset => asset.RelativeSourcePath == path) == false)
				{
					_assets.Add(Asset(AssetType.AnimationClip, Guid.NewGuid(), Path.GetFileName(path), path));
				}
			});

		var pipeline = Substitute.For<IProjectAssetPipelineService>();
		pipeline.GetDependencies(Arg.Any<string>()).Returns(Array.Empty<AssetDependencyRecord>());
		pipeline.TryGetPrimaryNodeIdForRelativeSourcePath(Arg.Any<string>(), Arg.Any<string>(), out Arg.Any<Guid>())
			.Returns(call =>
			{
				var found = _assets.FirstOrDefault(asset => asset.RelativeSourcePath == call.ArgAt<string>(1) && asset.Type == AssetType.AnimationClip);
				call[2] = found?.Id ?? Guid.Empty;
				return found is not null;
			});

		var workspaces = Substitute.For<IEditorWorkspaceService>();
		workspaces.Workspaces.Returns(Array.Empty<EditorWorkspace>());

		_selection = Substitute.For<IAssetSelectionService>();
		_session = new AnimationClipEditorSession(
			_project,
			pipeline,
			workspaces,
			_selection,
			null!,
			null!,
			null!);
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
	public void ImportedTimeline_CanEditUndoSaveAndReopen_WhileMotionStaysReadOnly()
	{
		Assert.That(_session.Open(_importedClipAsset), Is.True);
		Assert.That(_session.CanEdit, Is.False);
		Assert.That(_session.CanEditMetadata, Is.True);
		var document = _session.MetadataDocument!;
		document.SetMetadata(new AnimationClipMetadata { Markers = [new() { Name = "Step", Time = .5f }] });
		Assert.That(_session.GetRuntimeClip()!.Markers.Single().Name, Is.EqualTo("Step"));
		Assert.That(_session.Close(), Is.False);
		_session.ResolvePending(UnsavedChangesPolicy.Discard);
		Assert.That(_session.Open(_importedClipAsset), Is.True);
		Assert.That(_session.MetadataDocument!.Clip.Markers, Is.Empty);

		document = _session.MetadataDocument;
		document.SetMetadata(new AnimationClipMetadata { Markers = [new() { Name = "Step", Time = .5f }] });
		Assert.That(_session.Undo(), Is.True);
		Assert.That(_session.GetRuntimeClip()!.Markers, Is.Empty);
		Assert.That(_session.Redo(), Is.True);
		Assert.That(_session.Save(), Is.True);
		_project.Received().RefreshAssetSource(_importedClipAsset.RelativeSourcePath);
		var metadata = new AssetMetadataStore().Load(Path.Combine(_root, _importedClipAsset.RelativeSourcePath + ".meta"));
		Assert.That(metadata.GetImportSettingsOrDefault(() => new ModelImportSettings())
			.AnimationClips["animation:0"].Markers.Single().Name, Is.EqualTo("Step"));
		Assert.That(_session.GetRuntimeClip()!.TransformTracks, Is.SameAs(_importedClip.TransformTracks));
		Assert.That(File.ReadAllText(Path.Combine(_root, _importedClipAsset.RelativeSourcePath)), Is.EqualTo("model"));
	}

	[Test]
	public void DuplicateImportedClip_CarriesCurrentAnnotationsIntoTheAuthoredSource()
	{
		_session.Open(_importedClipAsset);
		_session.MetadataDocument!.SetMetadata(new AnimationClipMetadata
		{
			Markers = [new() { Name = "Step", Time = .5f }]
		});

		Assert.That(_session.DuplicateAsEditable(_importedClipAsset, UnsavedChangesPolicy.Discard), Is.True);

		Assert.That(_session.CanEdit, Is.True);
		Assert.That(_session.Document!.Clip.Markers.Single().Name, Is.EqualTo("Step"));
		Assert.That(AuthoredAnimationClipJson.Read(_session.Document.Path).Markers.Single().Time, Is.EqualTo(.5f));
	}

	[Test]
	public void CreateClip_WritesAnEmptyClipAndOpensIt()
	{
		Assert.That(_session.CreateClip("Assets/Characters", "Sword Attack", _skeletonAsset.Id), Is.True);

		Assert.That(_session.ClipName, Is.EqualTo("Sword Attack"));
		Assert.That(_session.CanEdit, Is.True);
		Assert.That(_session.Skeleton, Is.SameAs(_skeleton));
		Assert.That(File.Exists(Path.Combine(_root, "Assets/Characters/Sword Attack.anim.json")), Is.True);
	}

	[Test]
	public void CreateClip_NeverOverwritesAnExistingFile()
	{
		_session.CreateClip("Assets/Characters", "Idle", _skeletonAsset.Id, UnsavedChangesPolicy.Discard);
		_session.CreateClip("Assets/Characters", "Idle", _skeletonAsset.Id, UnsavedChangesPolicy.Discard);

		Assert.That(_session.ClipName, Is.EqualTo("Idle 1"));
	}

	[Test]
	public void CreateClip_WithoutSkeletonAsksForOne()
	{
		_session.CreateClip("Assets", "Loose", Guid.Empty);

		Assert.That(_session.Skeleton, Is.Null);
		Assert.That(_session.Diagnostic, Does.Contain("Choose a skeleton"));

		_session.SetSkeleton(_skeletonAsset.Id);

		Assert.That(_session.Skeleton, Is.SameAs(_skeleton));
		Assert.That(_session.Diagnostic, Is.Null);
	}

	[Test]
	public void ChangingSkeleton_RebindsAndUndoRestoresTheOldBinding()
	{
		_session.CreateClip("Assets", "Swap", _skeletonAsset.Id);

		_session.SetSkeleton(_otherSkeletonAsset.Id);

		Assert.That(_session.Skeleton, Is.SameAs(_otherSkeleton));
		Assert.That(_session.PreviewModelId, Is.EqualTo(_otherModelAsset.Id));

		_session.Undo();

		Assert.That(_session.Skeleton, Is.SameAs(_skeleton));
		Assert.That(_session.PreviewModelId, Is.EqualTo(_modelAsset.Id));
	}

	[Test]
	public void DuplicateAsEditable_CopiesTheImportedKeysNextToTheSource()
	{
		Assert.That(_session.DuplicateAsEditable(_importedClipAsset), Is.True);

		Assert.That(_session.CanEdit, Is.True);
		Assert.That(_session.ClipName, Is.EqualTo("Wave"));
		Assert.That(_session.Skeleton, Is.SameAs(_skeleton));
		Assert.That(_session.Document!.GetKeyFrames("arm"), Is.EqualTo(new[] { 0, 15 }));
		Assert.That(File.Exists(Path.Combine(_root, "Assets/Characters/Wave.anim.json")), Is.True);
	}

	[Test]
	public void DuplicateAsEditable_StaysOnTheCopyWhileTheSourceIsStillSelected()
	{
		_selection.SelectedAssetId.Returns(_importedClipAsset.Id);
		_session.Open(_importedClipAsset);

		_session.DuplicateAsEditable(_importedClipAsset);
		_session.FollowAssetSelection();

		Assert.That(_session.CanEdit, Is.True);
		Assert.That(_session.AssetId, Is.Not.EqualTo(_importedClipAsset.Id));
	}

	[Test]
	public void SelectingAnotherClip_StillOpensIt()
	{
		_session.CreateClip("Assets", "First", _skeletonAsset.Id);

		_selection.SelectedAssetId.Returns(_importedClipAsset.Id);
		_session.FollowAssetSelection();

		Assert.That(_session.AssetId, Is.EqualTo(_importedClipAsset.Id));
	}

	[Test]
	public void OpeningAnotherClipWithUnsavedEdits_AsksFirst()
	{
		_session.CreateClip("Assets", "First", _skeletonAsset.Id);
		var first = _session.AssetId;
		_session.Document!.SetKey("arm", 3, Pose);

		Assert.That(_session.Open(_importedClipAsset), Is.False);

		Assert.That(_session.IsPromptPending, Is.True);
		Assert.That(_session.AssetId, Is.EqualTo(first));
	}

	[Test]
	public void DiscardingUnsavedEdits_OpensTheOtherClipAndRevertsTheFirst()
	{
		_session.CreateClip("Assets", "First", _skeletonAsset.Id);
		var first = _assets.Single(asset => asset.Id == _session.AssetId);
		_session.Document!.SetKey("arm", 3, Pose);
		_session.Open(_importedClipAsset);

		_session.ResolvePending(UnsavedChangesPolicy.Discard);

		Assert.That(_session.AssetId, Is.EqualTo(_importedClipAsset.Id));
		Assert.That(_session.IsPromptPending, Is.False);

		_session.Open(first);

		Assert.That(_session.Document!.Clip.TransformTracks, Is.Empty);
		Assert.That(_session.Document.IsDirty, Is.False);
	}

	[Test]
	public void SavingUnsavedEdits_WritesThemBeforeOpening()
	{
		_session.CreateClip("Assets", "First", _skeletonAsset.Id);
		var path = Path.Combine(_root, "Assets/First.anim.json");
		_session.Document!.SetKey("arm", 3, Pose);
		_session.Open(_importedClipAsset);

		_session.ResolvePending(UnsavedChangesPolicy.Save);

		Assert.That(_session.AssetId, Is.EqualTo(_importedClipAsset.Id));
		Assert.That(AuthoredAnimationClipJson.Read(path).FindBoneTrack("arm"), Is.Not.Null);
	}

	[Test]
	public void CloseWithUnsavedEdits_AsksAndCancelKeepsTheClip()
	{
		_session.CreateClip("Assets", "First", _skeletonAsset.Id);
		_session.Document!.SetKey("arm", 3, Pose);

		Assert.That(_session.Close(), Is.False);
		Assert.That(_session.IsPromptPending, Is.True);
		Assert.That(_session.HasClip, Is.True);

		Assert.That(_session.Close(UnsavedChangesPolicy.Discard), Is.True);
		Assert.That(_session.HasClip, Is.False);
	}

	[Test]
	public void CleanClip_SwitchesWithoutAsking()
	{
		_session.CreateClip("Assets", "First", _skeletonAsset.Id);

		Assert.That(_session.Open(_importedClipAsset), Is.True);
		Assert.That(_session.IsPromptPending, Is.False);
	}

	[Test]
	public void Factory_SanitizesNamesAndFindsTheSkeletonOfASelectedModel()
	{
		var path = AnimationClipAssetFactory.GetUniquePath(_project, "Assets/", "bad/name:here");

		Assert.That(path, Does.StartWith("Assets/bad_name"));
		Assert.That(path, Does.EndWith(".anim.json"));
		Assert.That(AnimationClipAssetFactory.FindSkeletonForAsset(_modelAsset, _assets), Is.EqualTo(_skeletonAsset.Id));
		Assert.That(AnimationClipAssetFactory.FindSkeletonForAsset(_skeletonAsset, _assets), Is.EqualTo(_skeletonAsset.Id));
		Assert.That(AnimationClipAssetFactory.FindSkeletonForAsset(_importedClipAsset, _assets), Is.EqualTo(Guid.Empty));
		Assert.That(AnimationClipAssetFactory.GetFolder("Assets/Characters/Rig.fbx"), Is.EqualTo("Assets/Characters"));
	}

	private static AssetDatabaseEntry Asset(AssetType type, Guid sourceId, string name, string relativeSourcePath) =>
		new()
		{
			Id = Guid.NewGuid(),
			SourceId = sourceId,
			Type = type,
			Name = name,
			RelativeSourcePath = relativeSourcePath
		};
}
