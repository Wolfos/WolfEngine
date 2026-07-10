using NSubstitute;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
using WolfEngine.Input;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class EditorCommandServiceTests
{
	[Test]
	public void EntitySelection_PreservesInsertionOrderAndAddsVisibleRange()
	{
		var world = new World(WorldTag.Authoring);
		var first = world.CreateEntity("First");
		var second = world.CreateEntity("Second");
		var third = world.CreateEntity("Third");
		var fourth = world.CreateEntity("Fourth");
		var visibleEntities = new[] { first, second, third, fourth };

		EditorGui.ReplaceEntitySelection(second, world, requestFocus: false);
		EditorGui.AddEntitySelection(fourth, world, requestFocus: false);
		EditorGui.AddEntitySelection(fourth, world, requestFocus: false);
		EditorGui.AddEntitySelectionRange(visibleEntities, first, world, requestFocus: false);

		Assert.That(EditorGui.SelectedEntities, Is.EqualTo(new[] { second, fourth, first, third }));
		Assert.That(EditorGui.SelectedEntity, Is.EqualTo(second));
		Assert.That(EditorGui.SelectionRangeAnchor, Is.EqualTo(first));
		EditorGui.ClearEntitySelection();
	}

	[Test]
	public void EntitySelection_PrunesDestroyedEntitiesAndKeepsFirstLiveEntity()
	{
		var world = new World(WorldTag.Authoring);
		var first = world.CreateEntity("First");
		var second = world.CreateEntity("Second");
		EditorGui.ReplaceEntitySelection(first, world, requestFocus: false);
		EditorGui.AddEntitySelection(second, world, requestFocus: false);

		world.DestroyEntity(first);
		EditorGui.RefreshSelectedEntity(world, requestFocus: false);

		Assert.That(EditorGui.SelectedEntities, Is.EqualTo(new[] { second }));
		Assert.That(EditorGui.SelectedEntity, Is.EqualTo(second));
		EditorGui.ClearEntitySelection();
	}

	[Test]
	public void ShortcutResolver_MapsSceneCommandsAndFocusedDelete()
	{
		Assert.That(
			EditorShortcutCommandResolver.Resolve(
				new EditorShortcutSnapshot(true, false, false, false, true, false, false, false, false, false, false, false),
				EditorFocusedWindow.None),
			Is.EqualTo(EditorShortcutCommand.NewScene));

		Assert.That(
			EditorShortcutCommandResolver.Resolve(
				new EditorShortcutSnapshot(true, false, false, false, false, true, false, false, false, false, false, false),
				EditorFocusedWindow.None),
			Is.EqualTo(EditorShortcutCommand.SaveScene));

		Assert.That(
			EditorShortcutCommandResolver.Resolve(
				new EditorShortcutSnapshot(true, false, false, false, false, false, true, false, false, false, false, false),
				EditorFocusedWindow.None),
			Is.EqualTo(EditorShortcutCommand.RefreshAssetDatabase));

		Assert.That(
			EditorShortcutCommandResolver.Resolve(
				new EditorShortcutSnapshot(true, false, false, false, false, false, false, true, false, false, false, false),
				EditorFocusedWindow.Entities),
			Is.EqualTo(EditorShortcutCommand.DuplicateFocusedSelection));

		Assert.That(
			EditorShortcutCommandResolver.Resolve(
				new EditorShortcutSnapshot(false, false, false, false, false, false, false, false, true, false, false, false),
				EditorFocusedWindow.Entities),
			Is.EqualTo(EditorShortcutCommand.DeleteFocusedSelection));

		Assert.That(
			EditorShortcutCommandResolver.Resolve(
				new EditorShortcutSnapshot(true, false, false, false, false, false, false, false, false, true, false, true),
				EditorFocusedWindow.Assets),
			Is.EqualTo(EditorShortcutCommand.DeleteFocusedSelection));
	}

	[Test]
	public void ShortcutResolver_SuppressesShortcutsWhileTyping()
	{
		var command = EditorShortcutCommandResolver.Resolve(
			new EditorShortcutSnapshot(true, true, true, true, true, true, true, true, true, true, true, true),
			EditorFocusedWindow.Entities);

		Assert.That(command, Is.EqualTo(EditorShortcutCommand.None));
	}

	[Test]
	public void ShortcutResolver_MapsUndoAndRedo()
	{
		Assert.That(
			EditorShortcutCommandResolver.Resolve(
				new EditorShortcutSnapshot(true, false, true, false, false, false, false, false, false, false, false, false),
				EditorFocusedWindow.None),
			Is.EqualTo(EditorShortcutCommand.Undo));

		Assert.That(
			EditorShortcutCommandResolver.Resolve(
				new EditorShortcutSnapshot(true, true, true, false, false, false, false, false, false, false, false, true),
				EditorFocusedWindow.None),
			Is.EqualTo(EditorShortcutCommand.Redo));

		Assert.That(
			EditorShortcutCommandResolver.Resolve(
				new EditorShortcutSnapshot(true, false, false, true, false, false, false, false, false, false, false, false),
				EditorFocusedWindow.None),
			Is.EqualTo(EditorShortcutCommand.Redo));
	}

	[Test]
	public void RequestNewScene_WithDirtyScene_QueuesConfirmationUntilConfirmed()
	{
		var fixture = CreateCommandFixture();
		fixture.InteractionState.MarkSceneDirty();

		var completedImmediately = fixture.Service.RequestNewScene();

		Assert.That(completedImmediately, Is.False);
		Assert.That(fixture.Service.HasPendingSceneReplacement, Is.True);
		Assert.That(fixture.Service.PendingSceneReplacementType, Is.EqualTo(PendingSceneReplacementKind.NewScene));
		fixture.SceneWorkspace.DidNotReceive().ResetToNewScene();
	}

	[Test]
	public void ResolvePendingSceneReplacement_SaveThenNewScene_SavesAndClearsDirtyState()
	{
		var fixture = CreateCommandFixture();
		fixture.ProjectService.HasOpenProject.Returns(true);
		fixture.InteractionState.MarkSceneDirty();
		fixture.Service.RequestNewScene();

		var resolved = fixture.Service.ResolvePendingSceneReplacement(PendingSceneReplacementDecision.Save);

		Assert.That(resolved, Is.True);
		fixture.SceneWorkspace.Received(1).SaveCurrentScene();
		fixture.SceneWorkspace.Received(1).ResetToNewScene();
		Assert.That(fixture.InteractionState.IsSceneDirty, Is.False);
		Assert.That(fixture.Service.HasPendingSceneReplacement, Is.False);
	}

	[Test]
	public void RequestLoadScene_WithDirtyScene_UsesSameConfirmationFlow()
	{
		var fixture = CreateCommandFixture();
		var sceneId = Guid.NewGuid();
		fixture.InteractionState.MarkSceneDirty();

		fixture.Service.RequestLoadScene(sceneId);
		var resolved = fixture.Service.ResolvePendingSceneReplacement(PendingSceneReplacementDecision.Discard);

		Assert.That(resolved, Is.True);
		fixture.SceneWorkspace.Received(1).LoadScene(sceneId);
		Assert.That(fixture.Service.HasPendingSceneReplacement, Is.False);
		Assert.That(fixture.InteractionState.IsSceneDirty, Is.False);
	}

	[Test]
	public void ResolvePendingSceneReplacement_Cancel_KeepsCurrentScene()
	{
		var fixture = CreateCommandFixture();
		fixture.InteractionState.MarkSceneDirty();
		fixture.Service.RequestNewScene();

		var resolved = fixture.Service.ResolvePendingSceneReplacement(PendingSceneReplacementDecision.Cancel);

		Assert.That(resolved, Is.False);
		fixture.SceneWorkspace.DidNotReceive().SaveCurrentScene();
		fixture.SceneWorkspace.DidNotReceive().ResetToNewScene();
		Assert.That(fixture.Service.HasPendingSceneReplacement, Is.False);
		Assert.That(fixture.InteractionState.IsSceneDirty, Is.True);
	}

	[Test]
	public void DeleteFocusedSelection_RoutesToFocusedHandlerOnly()
	{
		var fixture = CreateCommandFixture();
		var entityHandler = new TrackingEntityDeletionHandler();
		var assetHandler = new TrackingAssetDeletionHandler();
		fixture.Service.BindDeletionHandlers(entityHandler, assetHandler);

		fixture.InteractionState.BeginFrame();
		fixture.InteractionState.SetFocusedWindow(EditorFocusedWindow.Entities);
		Assert.That(fixture.Service.DeleteFocusedSelection(), Is.True);
		Assert.That(entityHandler.CallCount, Is.EqualTo(1));
		Assert.That(assetHandler.CallCount, Is.EqualTo(0));

		fixture.InteractionState.BeginFrame();
		fixture.InteractionState.SetFocusedWindow(EditorFocusedWindow.Assets);
		Assert.That(fixture.Service.DeleteFocusedSelection(), Is.True);
		Assert.That(entityHandler.CallCount, Is.EqualTo(1));
		Assert.That(assetHandler.CallCount, Is.EqualTo(1));

		fixture.InteractionState.BeginFrame();
		fixture.InteractionState.SetFocusedWindow(EditorFocusedWindow.None);
		Assert.That(fixture.Service.DeleteFocusedSelection(), Is.False);
		Assert.That(entityHandler.CallCount, Is.EqualTo(1));
		Assert.That(assetHandler.CallCount, Is.EqualTo(1));
	}

	[Test]
	public void DuplicateFocusedSelection_RoutesToEntitiesHandlerOnly()
	{
		var fixture = CreateCommandFixture();
		var entityHandler = new TrackingEntityDeletionHandler();
		var assetHandler = new TrackingAssetDeletionHandler();
		fixture.Service.BindDeletionHandlers(entityHandler, assetHandler);

		fixture.InteractionState.BeginFrame();
		fixture.InteractionState.SetFocusedWindow(EditorFocusedWindow.Entities);
		Assert.That(fixture.Service.DuplicateFocusedSelection(), Is.True);
		Assert.That(entityHandler.DuplicateCallCount, Is.EqualTo(1));
		Assert.That(assetHandler.CallCount, Is.EqualTo(0));

		fixture.InteractionState.BeginFrame();
		fixture.InteractionState.SetFocusedWindow(EditorFocusedWindow.Assets);
		Assert.That(fixture.Service.DuplicateFocusedSelection(), Is.False);
		Assert.That(entityHandler.DuplicateCallCount, Is.EqualTo(1));
		Assert.That(assetHandler.CallCount, Is.EqualTo(0));
	}

	[Test]
	public void InteractionState_BeginFrame_DoesNotClearTrackedDeleteTarget()
	{
		var state = new EditorInteractionState();
		state.SetFocusedWindow(EditorFocusedWindow.Entities);

		state.BeginFrame();

		Assert.That(state.FocusedWindow, Is.EqualTo(EditorFocusedWindow.Entities));
	}

	[Test]
	public void SaveAndRefresh_RespectPreconditions()
	{
		var fixture = CreateCommandFixture();

		Assert.That(fixture.Service.SaveScene(), Is.False);
		Assert.That(fixture.Service.RefreshAssetDatabase(), Is.False);

		fixture.ProjectService.HasOpenProject.Returns(true);
		Assert.That(fixture.Service.SaveScene(), Is.True);
		Assert.That(fixture.Service.RefreshAssetDatabase(), Is.True);
		fixture.SceneWorkspace.Received(1).SaveCurrentScene();
		fixture.AssetRefreshService.Received(1).RefreshOpenSceneAssets();

		fixture.PlaySession.IsActive.Returns(true);
		Assert.That(fixture.Service.SaveScene(), Is.False);
		Assert.That(fixture.Service.RefreshAssetDatabase(), Is.False);
	}

	[Test]
	public void EntitiesWindow_DeleteSelectedEntity_RemovesEntityAndMarksSceneDirty()
	{
		var interactionState = new EditorInteractionState();
			var window = new EntitiesWindow(
				Substitute.For<IIconManager>(),
				interactionState,
				Substitute.For<IEditorSceneSnapshotService>(),
				Substitute.For<IEditorUndoRedoService>(),
				Substitute.For<IPrefabAssetCreator>(),
				Substitute.For<IAssetSelectionService>(),
				Substitute.For<IEditorNotificationService>());
		var scene = new EditorScene();
		var entity = scene.World.CreateEntity("Entity");
		EditorGui.SelectEntity(entity, scene.World, requestFocus: false);

		var deleted = window.DeleteSelectedEntity(scene);

		Assert.That(deleted, Is.True);
		Assert.That(scene.World.IsAlive(entity), Is.False);
		Assert.That(EditorGui.HasSelectedEntity, Is.False);
		Assert.That(interactionState.IsSceneDirty, Is.True);
	}

	[Test]
	public void AssetsWindow_RequestDeleteSelectedItem_TargetsSelectedAssetSource()
	{
		var projectService = new AssetLookupProjectService();
		var selectionService = new AssetSelectionService();
		var selectedAssetId = Guid.NewGuid();
		var asset = new AssetDatabaseEntry
		{
			Id = selectedAssetId,
			RelativeSourcePath = "Assets/Materials/Test.mat.json"
		};
		projectService.Register(asset);
		selectionService.Select(selectedAssetId);

		var window = new AssetsWindow(
			projectService,
			Substitute.For<IProjectAssetPipelineService>(),
			Substitute.For<IAssetThumbnailLoader>(),
			selectionService,
			Substitute.For<IEditorAssetHandlerRegistry>(),
			Substitute.For<IIconManager>(),
			new EditorInteractionState(),
			Substitute.For<IEditorCommandService>());

		var requested = window.RequestDeleteSelectedItem();

		Assert.That(requested, Is.True);
		Assert.That(window.PendingDeleteKindForTesting, Is.EqualTo("Source"));
		Assert.That(window.PendingDeleteRelativePathForTesting, Is.EqualTo("Assets/Materials/Test.mat.json"));
	}

	[Test]
	public void AssetsWindow_RequestDeleteSelectedItem_FallsBackToSelectedFolder()
	{
		var window = new AssetsWindow(
			Substitute.For<IEditorProjectService>(),
			Substitute.For<IProjectAssetPipelineService>(),
			Substitute.For<IAssetThumbnailLoader>(),
			new AssetSelectionService(),
			Substitute.For<IEditorAssetHandlerRegistry>(),
			Substitute.For<IIconManager>(),
			new EditorInteractionState(),
			Substitute.For<IEditorCommandService>());
		window.SetSelectedFolderForTesting("Assets/Data");

		var requested = window.RequestDeleteSelectedItem();

		Assert.That(requested, Is.True);
		Assert.That(window.PendingDeleteKindForTesting, Is.EqualTo("Folder"));
		Assert.That(window.PendingDeleteRelativePathForTesting, Is.EqualTo("Assets/Data"));
	}

	[Test]
	public void AssetsWindow_RequestRenameSelectedItem_TargetsSelectedAssetBeforeFolder()
	{
		var projectService = new AssetLookupProjectService();
		var selectionService = new AssetSelectionService();
		var selectedAssetId = Guid.NewGuid();
		var asset = new AssetDatabaseEntry
		{
			Id = selectedAssetId,
			RelativeSourcePath = "Assets/Materials/Test.mat.json"
		};
		projectService.Register(asset);
		selectionService.Select(selectedAssetId);

		var window = new AssetsWindow(
			projectService,
			Substitute.For<IProjectAssetPipelineService>(),
			Substitute.For<IAssetThumbnailLoader>(),
			selectionService,
			Substitute.For<IEditorAssetHandlerRegistry>(),
			Substitute.For<IIconManager>(),
			new EditorInteractionState(),
			Substitute.For<IEditorCommandService>());
		window.SetSelectedFolderForTesting("Assets/Data");

		var requested = window.RequestRenameSelectedItem();

		Assert.That(requested, Is.True);
		Assert.That(window.PendingRenameKindForTesting, Is.EqualTo("Source"));
		Assert.That(window.PendingRenameRelativePathForTesting, Is.EqualTo("Assets/Materials/Test.mat.json"));
	}

	[Test]
	public void AssetsWindow_RequestRenameSelectedItem_FallsBackToSelectedFolder()
	{
		var window = new AssetsWindow(
			Substitute.For<IEditorProjectService>(),
			Substitute.For<IProjectAssetPipelineService>(),
			Substitute.For<IAssetThumbnailLoader>(),
			new AssetSelectionService(),
			Substitute.For<IEditorAssetHandlerRegistry>(),
			Substitute.For<IIconManager>(),
			new EditorInteractionState(),
			Substitute.For<IEditorCommandService>());
		window.SetSelectedFolderForTesting("Assets/Data");

		var requested = window.RequestRenameSelectedItem();

		Assert.That(requested, Is.True);
		Assert.That(window.PendingRenameKindForTesting, Is.EqualTo("Folder"));
		Assert.That(window.PendingRenameRelativePathForTesting, Is.EqualTo("Assets/Data"));
	}

	private static CommandFixture CreateCommandFixture()
	{
		var sceneWorkspace = Substitute.For<IEditorSceneWorkspace>();
		sceneWorkspace.CurrentScene.Returns(new EditorScene());

		var projectService = Substitute.For<IEditorProjectService>();
		var assetRefreshService = Substitute.For<IEditorAssetRefreshService>();
		var playSession = Substitute.For<IEditorPlaySession>();
		playSession.IsActive.Returns(false);

		return new CommandFixture(
			sceneWorkspace,
			projectService,
			assetRefreshService,
			playSession,
			new EditorInteractionState(),
			Substitute.For<IEditorNotificationService>(),
			Substitute.For<IEditorUndoRedoService>());
	}

	private sealed record CommandFixture(
		IEditorSceneWorkspace SceneWorkspace,
		IEditorProjectService ProjectService,
		IEditorAssetRefreshService AssetRefreshService,
		IEditorPlaySession PlaySession,
		EditorInteractionState InteractionState,
		IEditorNotificationService NotificationService,
		IEditorUndoRedoService UndoRedoService)
	{
		public EditorCommandService Service { get; } = new(
			SceneWorkspace,
			ProjectService,
			AssetRefreshService,
			PlaySession,
			InteractionState,
			NotificationService,
			UndoRedoService,
			new InputSystem());
	}

	private sealed class TrackingEntityDeletionHandler : IEditorEntityDeletionHandler
	{
		public int CallCount { get; private set; }
		public int DuplicateCallCount { get; private set; }

		public bool DuplicateSelectedEntity(EditorScene scene)
		{
			DuplicateCallCount++;
			return true;
		}

		public bool DeleteSelectedEntity(EditorScene scene)
		{
			CallCount++;
			return true;
		}
	}

	private sealed class TrackingAssetDeletionHandler : IEditorAssetDeletionHandler
	{
		public int CallCount { get; private set; }

		public bool RequestDeleteSelectedItem()
		{
			CallCount++;
			return true;
		}
	}

	private sealed class AssetLookupProjectService : IEditorProjectService
	{
		private readonly Dictionary<Guid, AssetDatabaseEntry> _assets = new();

		public bool HasOpenProject => true;
		public string? ProjectRootPath => string.Empty;
		public string? AssetsPath => string.Empty;
		public string? LibraryPath => string.Empty;
		public string? DatabasePath => string.Empty;
		public string? GameplayProjectRelativePath => string.Empty;
		public string? GameplayProjectPath => string.Empty;
		public AssetDatabase CurrentAssetDatabase { get; } = new();

		public void Register(AssetDatabaseEntry asset)
		{
			_assets[asset.Id] = asset;
		}

		public bool CreateProject(string parentFolder, string projectName, out string errorMessage) => throw new NotSupportedException();
		public bool OpenProject(string projectRoot, out string errorMessage) => throw new NotSupportedException();
		public void CloseProject() => throw new NotSupportedException();
		public AssetDatabaseRefreshResult ReloadAssetDatabase() => throw new NotSupportedException();
		public void ReloadAssetDatabaseFromIndex() => throw new NotSupportedException();
		public void RefreshAssetSource(string relativeSourcePath) => throw new NotSupportedException();
		public void SaveAssetDatabase(AssetDatabase database) => throw new NotSupportedException();
		public AssetDatabase CloneCurrentAssetDatabase() => throw new NotSupportedException();

		public bool TryGetAsset(Guid assetId, out AssetDatabaseEntry asset)
		{
			if (_assets.TryGetValue(assetId, out var storedAsset))
			{
				asset = storedAsset;
				return true;
			}

			asset = null!;
			return false;
		}

		public string GetAbsolutePath(string relativePath) => throw new NotSupportedException();
		public void DeleteAssetSource(string relativeSourcePath) => throw new NotSupportedException();
		public void DeleteFolder(string relativeFolderPath) => throw new NotSupportedException();
		public string RenameAssetSource(string relativeSourcePath, string newName) => throw new NotSupportedException();
		public string RenameFolder(string relativeFolderPath, string newName) => throw new NotSupportedException();
		public string MoveAssetSourceToFolder(string relativeSourcePath, string targetFolderPath) => throw new NotSupportedException();
		public string MoveFolderToFolder(string relativeFolderPath, string targetFolderPath) => throw new NotSupportedException();
		public string CreateFolder(string parentFolderPath, string folderName) => throw new NotSupportedException();
	}
}
