using System.Numerics;
using WolfEngine.Animation;
using WolfEngine.Editor.UI;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class AnimationClipDocumentTests
{
	private string _directory = string.Empty;

	[SetUp]
	public void SetUp()
	{
		_directory = Path.Combine(Path.GetTempPath(), $"wolf-clip-document-{Guid.NewGuid():N}");
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
	public void ShorteningClip_TrimsAnnotations_UndoRestoresThemAndTracksTogether()
	{
		var document = CreateDocument();
		document.SetKey("arm", 30, Pose(1));
		document.SetMetadata(new AnimationClipMetadata
		{
			Markers = [new() { Name = "End", Time = 1 }],
			Curves = [new() { Name = "Weight", Times = [0, 1], Values = [0, 1] }]
		});
		Assert.That(document.CountKeysBeyond(15), Is.EqualTo(5));

		document.SetLengthFrames(15);

		Assert.That(document.Clip.Markers, Is.Empty);
		Assert.That(document.Clip.Curves[0].Times, Is.EqualTo(new[] { 0f }));
		document.Undo();
		Assert.That(document.Clip.Markers.Single().Name, Is.EqualTo("End"));
		Assert.That(document.Clip.Curves[0].Times, Is.EqualTo(new[] { 0f, 1f }));
		Assert.That(document.HasKey("arm", 30), Is.True);
	}

	[Test]
	public void SetKey_WritesEveryChannelOnTheFrame()
	{
		var document = CreateDocument();

		document.SetKey("arm", 10, Pose(1.0f));

		var track = document.Clip.FindBoneTrack("arm")!;
		Assert.That(track.Position.Frames, Is.EqualTo(new[] { 10 }));
		Assert.That(track.Rotation.Frames, Is.EqualTo(new[] { 10 }));
		Assert.That(track.Scale.Frames, Is.EqualTo(new[] { 10 }));
		Assert.That(document.GetKeyFrames("arm"), Is.EqualTo(new[] { 10 }));
	}

	[Test]
	public void SetKey_OverwritesAnExistingKey()
	{
		var document = CreateDocument();
		document.SetKey("arm", 10, Pose(1.0f));

		document.SetKey("arm", 10, Pose(2.0f));

		Assert.That(document.Clip.FindBoneTrack("arm")!.Position.Values, Is.EqualTo(new[] { new Vector3(2.0f, 0.0f, 0.0f) }));
	}

	[Test]
	public void SetKey_RejectsFramesOutsideTheClip()
	{
		var document = CreateDocument();

		Assert.Throws<ArgumentOutOfRangeException>(() => document.SetKey("arm", 31, Pose(1.0f)));
		Assert.Throws<ArgumentOutOfRangeException>(() => document.SetKey("arm", -1, Pose(1.0f)));
	}

	[Test]
	public void Undo_RemovesACreatedTrackAndRedoRestoresIt()
	{
		var document = CreateDocument();
		document.SetKey("arm", 10, Pose(1.0f));

		document.Undo();

		Assert.That(document.Clip.TransformTracks, Is.Empty);
		Assert.That(document.CanRedo, Is.True);

		document.Redo();

		Assert.That(document.GetKeyFrames("arm"), Is.EqualTo(new[] { 10 }));
	}

	[Test]
	public void Transaction_GroupsEditsIntoOneUndoStep()
	{
		var document = CreateDocument();

		using (document.BeginTransaction("Rotate Bone"))
		{
			document.SetKey("arm", 5, Pose(1.0f));
			document.SetKey("arm", 5, Pose(2.0f));
			document.SetKey("leg", 5, Pose(3.0f));
		}

		Assert.That(document.UndoDescription, Is.EqualTo("Rotate Bone"));

		document.Undo();

		Assert.That(document.Clip.TransformTracks, Is.Empty);
		Assert.That(document.CanUndo, Is.False);
	}

	[Test]
	public void EditThatChangesNothing_AddsNoUndoStep()
	{
		var document = CreateDocument();
		document.SetKey("arm", 5, Pose(1.0f));

		document.SetKey("arm", 5, Pose(1.0f));
		document.SetLoop(document.Clip.Loop);

		document.Undo();

		Assert.That(document.CanUndo, Is.False);
	}

	[Test]
	public void UndoInsideTransaction_Throws()
	{
		var document = CreateDocument();

		using (document.BeginTransaction("Drag"))
		{
			Assert.Throws<InvalidOperationException>(document.Undo);
		}
	}

	[Test]
	public void Undo_RestoresTrackOrder()
	{
		var document = CreateDocument();
		document.SetKey("a", 0, Pose(1.0f));
		document.SetKey("b", 0, Pose(1.0f));
		document.SetKey("c", 0, Pose(1.0f));

		document.DeleteKeys([new AnimationKeyRef("b", 0)]);
		document.Undo();

		Assert.That(document.Clip.TransformTracks.Select(track => track.Path), Is.EqualTo(new[] { "a", "b", "c" }));
	}

	[Test]
	public void DeleteKeys_RemovesTheTrackWhenItsLastKeyGoes()
	{
		var document = CreateDocument();
		document.SetKey("arm", 5, Pose(1.0f));
		document.SetKey("arm", 10, Pose(2.0f));

		Assert.That(document.DeleteKeys([new AnimationKeyRef("arm", 5)]), Is.True);
		Assert.That(document.GetKeyFrames("arm"), Is.EqualTo(new[] { 10 }));

		document.DeleteKeys([new AnimationKeyRef("arm", 10)]);

		Assert.That(document.Clip.FindBoneTrack("arm"), Is.Null);
	}

	[Test]
	public void DeleteKeys_ReturnsFalseWhenNothingWasKeyed()
	{
		var document = CreateDocument();

		Assert.That(document.DeleteKeys([new AnimationKeyRef("arm", 5)]), Is.False);
		Assert.That(document.CanUndo, Is.False);
	}

	[Test]
	public void MoveKeys_ShiftsSelectionAndOverwritesTheDestination()
	{
		var document = CreateDocument();
		document.SetKey("arm", 5, Pose(1.0f));
		document.SetKey("arm", 10, Pose(2.0f));
		document.SetKey("arm", 15, Pose(3.0f));

		var moved = document.MoveKeys([new AnimationKeyRef("arm", 5), new AnimationKeyRef("arm", 10)], 5);

		Assert.That(moved, Is.True);
		Assert.That(document.GetKeyFrames("arm"), Is.EqualTo(new[] { 10, 15 }));
		var position = document.Clip.FindBoneTrack("arm")!.Position;
		Assert.That(position.Values, Is.EqualTo(new[] { new Vector3(1.0f, 0.0f, 0.0f), new Vector3(2.0f, 0.0f, 0.0f) }));
	}

	[Test]
	public void MoveKeys_RefusesToLeaveTheClip()
	{
		var document = CreateDocument();
		document.SetKey("arm", 28, Pose(1.0f));

		Assert.That(document.MoveKeys([new AnimationKeyRef("arm", 28)], 5), Is.False);
		Assert.That(document.GetKeyFrames("arm"), Is.EqualTo(new[] { 28 }));
		Assert.That(document.UndoDescription, Is.EqualTo("Set Key"));
	}

	[Test]
	public void MoveKeys_MovesChannelsKeyedOnDifferentFrames()
	{
		var track = AuthoredTransformTrack.ForBone("arm");
		track.Rotation.Set(4, Quaternion.Identity);
		track.Position.Set(8, Vector3.One);
		var document = CreateDocument(track);

		document.MoveKeys([new AnimationKeyRef("arm", 4)], 2);

		var moved = document.Clip.FindBoneTrack("arm")!;
		Assert.That(moved.Rotation.Frames, Is.EqualTo(new[] { 6 }));
		Assert.That(moved.Position.Frames, Is.EqualTo(new[] { 8 }));
	}

	[Test]
	public void DuplicateKeys_KeepsTheOriginals()
	{
		var document = CreateDocument();
		document.SetKey("arm", 0, Pose(1.0f));

		document.DuplicateKeys([new AnimationKeyRef("arm", 0)], 30);

		Assert.That(document.GetKeyFrames("arm"), Is.EqualTo(new[] { 0, 30 }));
		Assert.That(document.UndoDescription, Is.EqualTo("Duplicate Keys"));
	}

	[Test]
	public void SetFramesPerSecond_KeepsKeyTimesAndSnaps()
	{
		var document = CreateDocument();
		document.SetKey("arm", 0, Pose(1.0f));
		document.SetKey("arm", 15, Pose(2.0f));
		document.SetKey("arm", 30, Pose(3.0f));

		document.SetFramesPerSecond(24);

		Assert.That(document.Clip.FramesPerSecond, Is.EqualTo(24));
		Assert.That(document.Clip.LengthFrames, Is.EqualTo(24));
		Assert.That(document.GetKeyFrames("arm"), Is.EqualTo(new[] { 0, 12, 24 }));
	}

	[Test]
	public void SetFramesPerSecond_KeepsTheClosestKeyOnCollision()
	{
		var document = CreateDocument();
		document.SetKey("arm", 9, Pose(1.0f));
		document.SetKey("arm", 10, Pose(2.0f));

		// At 10 FPS, frame 9 lands on 3.0 and frame 10 on 3.33; both snap to frame 3.
		document.SetFramesPerSecond(10);

		Assert.That(document.GetKeyFrames("arm"), Is.EqualTo(new[] { 3 }));
		Assert.That(document.Clip.FindBoneTrack("arm")!.Position.Values[0].X, Is.EqualTo(1.0f));
	}

	[Test]
	public void SetFramesPerSecond_UndoRestoresFramesAndRate()
	{
		var document = CreateDocument();
		document.SetKey("arm", 15, Pose(1.0f));
		document.SetFramesPerSecond(60);

		document.Undo();

		Assert.That(document.Clip.FramesPerSecond, Is.EqualTo(30));
		Assert.That(document.Clip.LengthFrames, Is.EqualTo(30));
		Assert.That(document.GetKeyFrames("arm"), Is.EqualTo(new[] { 15 }));
	}

	[Test]
	public void SetLengthFrames_DeletesKeysPastTheNewEnd()
	{
		var document = CreateDocument();
		document.SetKey("arm", 10, Pose(1.0f));
		document.SetKey("arm", 25, Pose(2.0f));
		document.SetKey("leg", 25, Pose(2.0f));

		Assert.That(document.CountKeysBeyond(20), Is.EqualTo(6));

		document.SetLengthFrames(20);

		Assert.That(document.GetKeyFrames("arm"), Is.EqualTo(new[] { 10 }));
		Assert.That(document.Clip.FindBoneTrack("leg"), Is.Null);

		document.Undo();

		Assert.That(document.Clip.LengthFrames, Is.EqualTo(30));
		Assert.That(document.GetKeyFrames("leg"), Is.EqualTo(new[] { 25 }));
	}

	[Test]
	public void DirtyState_FollowsSaveAndUndo()
	{
		var path = Path.Combine(_directory, "Clip.anim.json");
		AuthoredAnimationClipJson.Write(path, new AuthoredAnimationClipFile());
		var document = AnimationClipDocument.Open(path);

		Assert.That(document.IsDirty, Is.False);

		document.SetKey("arm", 0, Pose(1.0f));

		Assert.That(document.IsDirty, Is.True);

		document.Save();

		Assert.That(document.IsDirty, Is.False);

		document.Undo();

		Assert.That(document.IsDirty, Is.True);

		document.Redo();

		Assert.That(document.IsDirty, Is.False);
	}

	[Test]
	public void CreateNew_StartsDirtyUntilSaved()
	{
		var skeletonId = Guid.NewGuid();
		var path = Path.Combine(_directory, "New.anim.json");
		var document = AnimationClipDocument.CreateNew(path, skeletonId);

		Assert.That(document.IsDirty, Is.True);

		document.Save();

		Assert.That(document.IsDirty, Is.False);
		Assert.That(AuthoredAnimationClipJson.Read(path).Skeleton.NodeId, Is.EqualTo(skeletonId));
	}

	[Test]
	public void Save_RoundTripsEditsAndUntouchedPropertyTracks()
	{
		var path = Path.Combine(_directory, "Clip.anim.json");
		AuthoredAnimationClipJson.Write(path, new AuthoredAnimationClipFile
		{
			PropertyTracks =
			[
				new AuthoredPropertyTrack
				{
					Path = "Light",
					Property = "Intensity",
					Times = [0.0f],
					Values = [2.0f]
				}
			]
		});
		var document = AnimationClipDocument.Open(path);
		document.SetKey("arm", 3, Pose(1.0f));

		document.Save();
		var reopened = AnimationClipDocument.Open(path);

		Assert.That(reopened.GetKeyFrames("arm"), Is.EqualTo(new[] { 3 }));
		Assert.That(reopened.Clip.PropertyTracks.Single().Values, Is.EqualTo(new[] { 2.0f }));
	}

	[Test]
	public void Revision_ChangesOnEditUndoAndRedo()
	{
		var document = CreateDocument();
		var revisions = new List<long>
		{
			document.Revision
		};

		document.SetKey("arm", 0, Pose(1.0f));
		revisions.Add(document.Revision);
		document.Undo();
		revisions.Add(document.Revision);
		document.Redo();
		revisions.Add(document.Revision);

		Assert.That(revisions, Is.Unique);
	}

	private AnimationClipDocument CreateDocument(params AuthoredTransformTrack[] tracks)
	{
		var clip = new AuthoredAnimationClipFile
		{
			FramesPerSecond = 30,
			LengthFrames = 30,
			TransformTracks = [.. tracks]
		};

		return new AnimationClipDocument(Path.Combine(_directory, "Test.anim.json"), clip, existsOnDisk: true);
	}

	private static BoneTransform Pose(float x) =>
		new(new Vector3(x, 0.0f, 0.0f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, x * 0.1f), Vector3.One);
}
