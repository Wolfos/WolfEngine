using NSubstitute;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class EditorUndoRedoServiceTests
{
	[Test]
	public void CommitCapture_DuringPlayMode_DropsAuthoringSceneEntries()
	{
		var fixture = CreateFixture();

		fixture.PlaySession.IsActive.Returns(true);
		fixture.Service.BeginCapture("Delete Entity");
		var committed = fixture.Service.CommitCapture(CreateSceneEntry());

		Assert.That(committed, Is.False);
		Assert.That(fixture.Service.CanUndo, Is.False);

		// The recorded history must stay empty after play mode stops, otherwise undo would replay a
		// runtime-only edit against the authoring scene.
		fixture.PlaySession.IsActive.Returns(false);
		Assert.That(fixture.Service.CanUndo, Is.False);
		Assert.That(fixture.Service.Undo(), Is.False);
		fixture.SceneSnapshotService.DidNotReceiveWithAnyArgs().RestoreDeletedEntities(default!, default!);
	}

	[Test]
	public void CommitCapture_DuringPlayMode_KeepsAssetEntries()
	{
		var fixture = CreateFixture();
		fixture.PlaySession.IsActive.Returns(true);

		var committed = fixture.Service.CommitCapture(new MaterialAssetEditUndoRedoEntry(
			"Edit Material Asset",
			new EditorAssetFileSnapshot(Guid.NewGuid(), "Assets/M.mat.json", "Assets/M.mat.json", "{}"),
			new EditorAssetFileSnapshot(Guid.NewGuid(), "Assets/M.mat.json", "Assets/M.mat.json", "{ }")));

		Assert.That(committed, Is.True);
		Assert.That(fixture.Service.CanUndo, Is.True);
		Assert.That(fixture.Service.Undo(), Is.True);
		fixture.AssetSnapshotService.ReceivedWithAnyArgs(1).ApplyMaterialAssetSnapshot(default);
	}

	[Test]
	public void Undo_DuringPlayMode_DoesNotReplayAuthoringSceneEntryRecordedBeforePlay()
	{
		var fixture = CreateFixture();
		Assert.That(fixture.Service.CommitCapture(CreateSceneEntry()), Is.True);

		fixture.PlaySession.IsActive.Returns(true);

		Assert.That(fixture.Service.CanUndo, Is.False);
		Assert.That(fixture.Service.Undo(), Is.False);
		fixture.SceneSnapshotService.DidNotReceiveWithAnyArgs().RestoreDeletedEntities(default!, default!);

		// Stopping play mode makes the pre-play history usable again.
		fixture.PlaySession.IsActive.Returns(false);
		Assert.That(fixture.Service.CanUndo, Is.True);
		Assert.That(fixture.Service.Undo(), Is.True);
		fixture.SceneSnapshotService.ReceivedWithAnyArgs(1).RestoreDeletedEntities(default!, default!);
	}

	[Test]
	public void Redo_DuringPlayMode_DoesNotReplayAuthoringSceneEntry()
	{
		var fixture = CreateFixture();
		fixture.Service.CommitCapture(CreateSceneEntry());
		Assert.That(fixture.Service.Undo(), Is.True);

		fixture.PlaySession.IsActive.Returns(true);

		Assert.That(fixture.Service.CanRedo, Is.False);
		Assert.That(fixture.Service.Redo(), Is.False);
		fixture.SceneSnapshotService.DidNotReceiveWithAnyArgs().DeleteEntitiesByPersistentIds(default!, default!);
	}

	[Test]
	public void MarkSceneDirty_ForRuntimeWorld_LeavesAuthoringSceneClean()
	{
		var interactionState = new EditorInteractionState();

		interactionState.MarkSceneDirty(new World(WorldTag.Game));

		Assert.That(interactionState.IsSceneDirty, Is.False);

		interactionState.MarkSceneDirty(new World(WorldTag.Authoring));

		Assert.That(interactionState.IsSceneDirty, Is.True);
	}

	private static EntityDeletionUndoRedoEntry CreateSceneEntry()
		=> new("Delete Entity", [new DeletedEntitySnapshot(SceneCellKey.Global, new SavedEntity { EntityId = Guid.NewGuid() })]);

	private static UndoRedoFixture CreateFixture()
	{
		var playSession = Substitute.For<IEditorPlaySession>();
		playSession.IsActive.Returns(false);

		var sceneWorkspace = Substitute.For<IEditorSceneWorkspace>();
		sceneWorkspace.CurrentScene.Returns(new EditorScene());

		var sceneSnapshotService = Substitute.For<IEditorSceneSnapshotService>();
		var assetSnapshotService = Substitute.For<IEditorAssetSnapshotService>();

		return new UndoRedoFixture(
			new EditorUndoRedoService(
				sceneWorkspace,
				new EditorInteractionState(),
				sceneSnapshotService,
				assetSnapshotService,
				Substitute.For<ITerrainAssetPersistenceService>(),
				playSession),
			playSession,
			sceneSnapshotService,
			assetSnapshotService);
	}

	private sealed record UndoRedoFixture(
		EditorUndoRedoService Service,
		IEditorPlaySession PlaySession,
		IEditorSceneSnapshotService SceneSnapshotService,
		IEditorAssetSnapshotService AssetSnapshotService);
}
