using System;
using System.Collections.Generic;
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
	public required IReadOnlyList<Guid> SelectedEntityIds { get; init; }
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
			SelectedEntityIds = GetSelectedEntityIds(currentScene)
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
		RestoreSelectedEntities(restoredScene, snapshot.SelectedEntityIds);
	}

	private static IReadOnlyList<Guid> GetSelectedEntityIds(EditorScene scene)
	{
		var ids = new List<Guid>();
		foreach (var entity in EditorGui.SelectedEntities)
		{
			if (scene.World.IsAlive(entity) && scene.EntityIds.TryGetValue(entity, out var id) && id != Guid.Empty) ids.Add(id);
		}
		return ids;
	}

	private static void RestoreSelectedEntities(EditorScene scene, IReadOnlyList<Guid> selectedEntityIds)
	{
		EditorGui.ClearEntitySelection();
		foreach (var entityId in selectedEntityIds)
		{
			foreach (var entry in scene.EntityIds)
			{
				if (entry.Value == entityId)
				{
					EditorGui.AddEntitySelection(entry.Key, scene.World, requestFocus: false);
					break;
				}
			}
		}
	}
}
