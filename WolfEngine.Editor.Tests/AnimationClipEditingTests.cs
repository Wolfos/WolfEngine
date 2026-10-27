using System.Numerics;
using NSubstitute;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class AnimationClipEditingTests
{
	private static readonly BoneTransform Bent =
		new(new Vector3(0.0f, 1.0f, 0.0f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f), Vector3.One);

	private string _root = string.Empty;
	private Skeleton _skeleton = null!;
	private AssetDatabaseEntry _skeletonAsset = null!;
	private AssetDatabaseEntry _modelAsset = null!;
	private AssetDatabaseEntry _clipAsset = null!;
	private AssetDatabaseEntry _importedClipAsset = null!;
	private AnimationClip _importedClip = null!;
	private IEditorProjectService _project = null!;
	private AnimationClipEditorSession _session = null!;

	[SetUp]
	public void SetUp()
	{
		_root = Path.Combine(Path.GetTempPath(), $"wolf-clip-editing-{Guid.NewGuid():N}");
		Directory.CreateDirectory(Path.Combine(_root, "Assets"));

		_skeleton = new Skeleton(
			"rig",
			["root", "arm"],
			[-1, 0],
			[BoneTransform.Identity, new BoneTransform(new Vector3(1.0f, 0.0f, 0.0f), Quaternion.Identity, Vector3.One)],
			[Matrix4x4.Identity, Matrix4x4.Identity]);

		var rigSource = Guid.NewGuid();
		_skeletonAsset = Asset(AssetType.Skeleton, rigSource, "rig", "Assets/Rig.fbx");
		_modelAsset = Asset(AssetType.Model3D, rigSource, "Rig.fbx", "Assets/Rig.fbx");
		_clipAsset = Asset(AssetType.AnimationClip, Guid.NewGuid(), "Swing.anim.json", "Assets/Swing.anim.json");
		_importedClipAsset = Asset(AssetType.AnimationClip, rigSource, "Take", "Assets/Rig.fbx");
		_importedClip = new AnimationClip("Take", 1.0f, 30.0f, true, [], [], "rig", []);

		AuthoredAnimationClipJson.Write(Path.Combine(_root, _clipAsset.RelativeSourcePath), new AuthoredAnimationClipFile
		{
			Skeleton = new AssetRef<Skeleton>
			{
				NodeId = _skeletonAsset.Id
			}
		});

		var registry = Substitute.For<IAssetInstanceRegistry>();
		registry.GetInstance(_skeletonAsset.Id, typeof(Skeleton)).Returns(_skeleton);
		registry.GetInstance(_importedClipAsset.Id, typeof(AnimationClip)).Returns(_importedClip);
		AssetDatabase.SetInstanceRegistry(registry);

		_project = Substitute.For<IEditorProjectService>();
		_project.ProjectRootPath.Returns(_root);
		_project.CurrentAssetDatabase.Returns(new AssetDatabase
		{
			Assets = [_skeletonAsset, _modelAsset, _clipAsset, _importedClipAsset]
		});
		_project.GetAbsoluteAssetPath(Arg.Any<Guid>(), Arg.Any<string>())
			.Returns(call => Path.Combine(_root, call.ArgAt<string>(1)));
		_project.IsAssetReadOnly(Arg.Any<Guid>()).Returns(false);

		var pipeline = Substitute.For<IProjectAssetPipelineService>();
		pipeline.GetDependencies(Arg.Any<string>()).Returns(Array.Empty<AssetDependencyRecord>());

		var workspaces = Substitute.For<IEditorWorkspaceService>();
		workspaces.Workspaces.Returns(Array.Empty<EditorWorkspace>());

		_session = new AnimationClipEditorSession(
			_project,
			pipeline,
			workspaces,
			Substitute.For<IAssetSelectionService>(),
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
	public void Open_AuthoredClipIsEditableAndBoundToItsSkeleton()
	{
		Assert.That(_session.Open(_clipAsset), Is.True);

		Assert.That(_session.CanEdit, Is.True);
		Assert.That(_session.Skeleton, Is.SameAs(_skeleton));
		Assert.That(_session.PreviewModelId, Is.EqualTo(_modelAsset.Id));
		Assert.That(_session.ClipName, Is.EqualTo("Swing"));
	}

	[Test]
	public void Open_ImportedClipIsReadOnly()
	{
		_session.Open(_importedClipAsset);

		Assert.That(_session.IsReadOnly, Is.True);
		Assert.That(_session.CanEdit, Is.False);
		Assert.That(_session.SetBoneLocalTransform("arm", Bent), Is.False);
		Assert.That(_session.LengthFrames, Is.EqualTo(30));
	}

	[Test]
	public void EditWithoutAutoKey_IsAnUnkeyedOverride()
	{
		_session.Open(_clipAsset);

		_session.SetBoneLocalTransform("arm", Bent);

		Assert.That(_session.HasUnkeyedChange("arm"), Is.True);
		Assert.That(_session.TryGetBoneLocal("arm", out var local), Is.True);
		Assert.That(local.Rotation, Is.EqualTo(Bent.Rotation));
		Assert.That(_session.Document!.Clip.TransformTracks, Is.Empty);
		Assert.That(_session.Document.CanUndo, Is.False);
	}

	[Test]
	public void UntouchedBone_ReadsTheBindPose()
	{
		_session.Open(_clipAsset);

		Assert.That(_session.TryGetBoneLocal("arm", out var local), Is.True);
		Assert.That(local.Position, Is.EqualTo(new Vector3(1.0f, 0.0f, 0.0f)));
		Assert.That(_session.TryGetBoneLocal("missing", out _), Is.False);
	}

	[Test]
	public void MovingThePlayhead_DiscardsUnkeyedEdits()
	{
		_session.Open(_clipAsset);
		_session.SetBoneLocalTransform("arm", Bent);

		_session.SetPlayhead(10);

		Assert.That(_session.HasUnkeyedChanges, Is.False);
	}

	[Test]
	public void SettingThePlayheadToTheSameFrame_KeepsUnkeyedEdits()
	{
		_session.Open(_clipAsset);
		_session.SetBoneLocalTransform("arm", Bent);

		_session.SetPlayhead(0);

		Assert.That(_session.HasUnkeyedChange("arm"), Is.True);
	}

	[Test]
	public void StartingPlayback_DiscardsUnkeyedEdits()
	{
		_session.Open(_clipAsset);
		_session.SetBoneLocalTransform("arm", Bent);

		_session.Play();

		Assert.That(_session.HasUnkeyedChanges, Is.False);
	}

	[Test]
	public void SetKey_KeysTheUnkeyedEditAtThePlayhead()
	{
		_session.Open(_clipAsset);
		_session.SetPlayhead(12);
		_session.SetBoneLocalTransform("arm", Bent);

		Assert.That(_session.SetKey("arm"), Is.True);

		Assert.That(_session.HasUnkeyedChanges, Is.False);
		Assert.That(_session.Document!.GetKeyFrames("arm"), Is.EqualTo(new[] { 12 }));
		Assert.That(_session.TryGetBoneLocal("arm", out var local), Is.True);
		Assert.That(MathF.Abs(Quaternion.Dot(local.Rotation, Bent.Rotation)), Is.EqualTo(1.0f).Within(1e-5f));
	}

	[Test]
	public void SetKey_WithoutEditsKeysTheSampledPose()
	{
		_session.Open(_clipAsset);

		_session.SetKey("arm");

		Assert.That(_session.Document!.Clip.FindBoneTrack("arm")!.Position.Values.Single(), Is.EqualTo(new Vector3(1.0f, 0.0f, 0.0f)));
	}

	[Test]
	public void AutoKey_WritesAKeyForEveryEdit()
	{
		_session.Open(_clipAsset);
		_session.AutoKey = true;
		_session.SetPlayhead(5);

		_session.SetBoneLocalTransform("arm", Bent);

		Assert.That(_session.HasUnkeyedChanges, Is.False);
		Assert.That(_session.Document!.GetKeyFrames("arm"), Is.EqualTo(new[] { 5 }));
		Assert.That(_session.Document.UndoDescription, Is.EqualTo("Transform Bone"));
	}

	[Test]
	public void AutoKeyDrag_IsOneUndoStepAndShowsTheEditWhileDragging()
	{
		_session.Open(_clipAsset);
		_session.AutoKey = true;

		using (_session.BeginEdit("Rotate Bone"))
		{
			_session.SetBoneLocalTransform("arm", Bent with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.1f) });
			_session.SetBoneLocalTransform("arm", Bent);

			Assert.That(_session.TryGetBoneLocal("arm", out var during), Is.True);
			Assert.That(during.Rotation, Is.EqualTo(Bent.Rotation));
			Assert.That(_session.Undo(), Is.False);
		}

		Assert.That(_session.Document!.UndoDescription, Is.EqualTo("Rotate Bone"));
		_session.Undo();
		Assert.That(_session.Document.CanUndo, Is.False);
		Assert.That(_session.Document.Clip.TransformTracks, Is.Empty);
	}

	[Test]
	public void Undo_DoesNothingForImportedClips()
	{
		_session.Open(_importedClipAsset);

		Assert.That(_session.Undo(), Is.False);
		Assert.That(_session.Save(), Is.False);
	}

	[Test]
	public void Save_WritesTheClipAndReimportsIt()
	{
		_session.Open(_clipAsset);
		_session.AutoKey = true;
		_session.SetBoneLocalTransform("arm", Bent);

		Assert.That(_session.Save(), Is.True);

		Assert.That(_session.Document!.IsDirty, Is.False);
		var saved = AuthoredAnimationClipJson.Read(Path.Combine(_root, _clipAsset.RelativeSourcePath));
		Assert.That(saved.FindBoneTrack("arm"), Is.Not.Null);
		_project.Received(1).RefreshAssetSource(_clipAsset.RelativeSourcePath);
	}

	[Test]
	public void ShorteningTheClip_ClampsThePlayhead()
	{
		_session.Open(_clipAsset);
		_session.SetPlayhead(30);

		_session.SetLengthFrames(20);

		Assert.That(_session.PlayheadFrame, Is.EqualTo(20.0f));
	}

	[Test]
	public void Advance_LoopsWithinTheClip()
	{
		_session.Open(_clipAsset);
		_session.Play();

		_session.Advance(1.5f);

		Assert.That(_session.PlayheadFrame, Is.EqualTo(15.0f).Within(1e-3f));
		Assert.That(_session.IsPlaying, Is.True);
	}

	[Test]
	public void Advance_StopsAtTheEndOfANonLoopingClip()
	{
		_session.Open(_clipAsset);
		_session.SetLoop(false);
		_session.Play();

		_session.Advance(2.0f);

		Assert.That(_session.PlayheadFrame, Is.EqualTo(30.0f));
		Assert.That(_session.IsPlaying, Is.False);
	}

	[Test]
	public void BoneGizmo_RotationIsConvertedIntoParentSpace()
	{
		var parentRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.5f);
		var space = new FakeBoneSpace(Matrix4x4.CreateFromQuaternion(parentRotation) * Matrix4x4.CreateTranslation(0.0f, 2.0f, 0.0f));
		var sink = new FakeBoneSink();
		var target = new BoneGizmoTarget(space, sink, "arm", 1);
		var localRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f);
		var worldRotation = Quaternion.Concatenate(localRotation, parentRotation);

		target.SetWorldRotation(0, worldRotation);

		Assert.That(MathF.Abs(Quaternion.Dot(sink.Local.Rotation, localRotation)), Is.EqualTo(1.0f).Within(1e-5f));
	}

	[Test]
	public void BoneGizmo_PositionIsConvertedIntoParentSpace()
	{
		var space = new FakeBoneSpace(Matrix4x4.CreateScale(2.0f) * Matrix4x4.CreateTranslation(0.0f, 2.0f, 0.0f));
		var sink = new FakeBoneSink();
		var target = new BoneGizmoTarget(space, sink, "arm", 1);

		target.SetWorldPosition(0, new Vector3(4.0f, 2.0f, 0.0f));

		Assert.That(Vector3.Distance(sink.Local.Position, new Vector3(2.0f, 0.0f, 0.0f)), Is.LessThan(1e-5f));
	}

	[Test]
	public void BoneGizmo_DragIsOneEdit()
	{
		var sink = new FakeBoneSink();
		var target = new BoneGizmoTarget(new FakeBoneSpace(Matrix4x4.Identity), sink, "arm", 1);

		target.BeginEdit();
		target.SetLocalScale(0, new Vector3(2.0f));
		target.SetLocalScale(0, new Vector3(3.0f));
		target.EndEdit(commit: true);

		Assert.That(sink.BeginCount, Is.EqualTo(1));
		Assert.That(sink.OpenEdits, Is.Zero);
		Assert.That(sink.Local.Scale, Is.EqualTo(new Vector3(3.0f)));
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

	private sealed class FakeBoneSpace : IBoneSpace
	{
		private readonly Matrix4x4 _parentWorld;

		public FakeBoneSpace(Matrix4x4 parentWorld)
		{
			_parentWorld = parentWorld;
		}

		public Matrix4x4 GetBoneWorldMatrix(int boneIndex) => _parentWorld;

		public Matrix4x4 GetBoneParentWorldMatrix(int boneIndex) => _parentWorld;
	}

	private sealed class FakeBoneSink : IBoneEditSink
	{
		public BoneTransform Local { get; private set; } = BoneTransform.Identity;
		public int BeginCount { get; private set; }
		public int OpenEdits { get; private set; }

		public bool TryGetBoneLocal(string bone, out BoneTransform local)
		{
			local = Local;
			return true;
		}

		public bool SetBoneLocalTransform(string bone, BoneTransform local)
		{
			Local = local;
			return true;
		}

		public IDisposable BeginEdit(string description)
		{
			BeginCount++;
			OpenEdits++;
			return new Scope(this);
		}

		private sealed class Scope : IDisposable
		{
			private readonly FakeBoneSink _sink;

			public Scope(FakeBoneSink sink)
			{
				_sink = sink;
			}

			public void Dispose()
			{
				_sink.OpenEdits--;
			}
		}
	}
}
