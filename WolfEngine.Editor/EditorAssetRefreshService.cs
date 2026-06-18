using System;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;

namespace WolfEngine.Editor;

public interface IEditorAssetRefreshService
{
	EditorOpenSceneAssetRefreshSnapshot CaptureOpenSceneAssets();
	void RefreshOpenSceneAssets();
	void RefreshOpenSceneAssets(EditorOpenSceneAssetRefreshSnapshot snapshot);
}

public sealed class EditorOpenSceneAssetRefreshSnapshot
{
	public required EditorSceneReloadSnapshot SceneSnapshot { get; init; }
	public required WorldTag WorldTag { get; init; }
	public required Guid? SelectedEntityId { get; init; }
}

public sealed class EditorAssetRefreshService : IEditorAssetRefreshService
{
	private readonly IEditorProjectService _projectService;
	private readonly IEditorSceneWorkspace _sceneWorkspace;
	private readonly IEditorSceneReloadService _sceneReloadService;

	public EditorAssetRefreshService(
		IEditorProjectService projectService,
		IEditorSceneWorkspace sceneWorkspace,
		IEditorSceneReloadService sceneReloadService)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_sceneWorkspace = sceneWorkspace ?? throw new ArgumentNullException(nameof(sceneWorkspace));
		_sceneReloadService = sceneReloadService ?? throw new ArgumentNullException(nameof(sceneReloadService));
	}

	public EditorOpenSceneAssetRefreshSnapshot CaptureOpenSceneAssets()
	{
		var currentScene = _sceneWorkspace.CurrentScene;
		return new EditorOpenSceneAssetRefreshSnapshot
		{
			SceneSnapshot = _sceneReloadService.Capture(currentScene),
			WorldTag = currentScene.World.Tag,
			SelectedEntityId = TryGetSelectedEntityId(currentScene)
		};
	}

	public void RefreshOpenSceneAssets()
	{
		RefreshOpenSceneAssets(CaptureOpenSceneAssets());
	}

	public void RefreshOpenSceneAssets(EditorOpenSceneAssetRefreshSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		_projectService.ReloadAssetDatabase();

		var restoredScene = _sceneReloadService.Restore(snapshot.SceneSnapshot, snapshot.WorldTag);
		_sceneWorkspace.ReplaceCurrentScene(restoredScene);
		RestoreSelectedEntity(restoredScene, snapshot.SelectedEntityId);
	}

	private static Guid? TryGetSelectedEntityId(EditorScene scene)
	{
		return EditorGui.HasSelectedEntity &&
		       scene.World.IsAlive(EditorGui.SelectedEntity) &&
		       scene.EntityIds.TryGetValue(EditorGui.SelectedEntity, out var selectedEntityId) &&
		       selectedEntityId != Guid.Empty
			? selectedEntityId
			: null;
	}

	private static void RestoreSelectedEntity(EditorScene scene, Guid? selectedEntityId)
	{
		if (selectedEntityId is not { } entityId)
		{
			EditorGui.ClearEntitySelection();
			return;
		}

		foreach (var entry in scene.EntityIds)
		{
			if (entry.Value == entityId)
			{
				EditorGui.SelectEntity(entry.Key, scene.World, requestFocus: false);
				return;
			}
		}

		EditorGui.ClearEntitySelection();
	}
}
