using System;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;

namespace WolfEngine.Editor;

public interface IEditorAssetRefreshService
{
	void RefreshOpenSceneAssets();
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

	public void RefreshOpenSceneAssets()
	{
		var currentScene = _sceneWorkspace.CurrentScene;
		var snapshot = _sceneReloadService.Capture(currentScene);
		var selectedEntityId = TryGetSelectedEntityId(currentScene);

		_projectService.ReloadAssetDatabase();

		var restoredScene = _sceneReloadService.Restore(snapshot, currentScene.World.Tag);
		_sceneWorkspace.ReplaceCurrentScene(restoredScene);
		RestoreSelectedEntity(restoredScene, selectedEntityId);
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
