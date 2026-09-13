using WolfEngine.Profiling;

namespace WolfEngine.Editor.UI;

public sealed record EditorWindowDescriptor(string Id, string DisplayName, EditorWindow Window);

public sealed class EditorWindowRegistry
{
	private readonly IEditorWorkspaceService _workspaces;
	private readonly IEditorNotificationService _notifications;
	private readonly EditorWindowDescriptor[] _windows;

	public EditorWindowRegistry(
		IEditorWorkspaceService workspaces,
		IEditorNotificationService notifications,
		SceneWindow scene,
		EntitiesWindow entities,
		ComponentsWindow components,
		AssetsWindow assets,
		AssetEditorWindow assetEditor,
		LogWindow log,
		ProfilerWindow profiler,
		MaterialImporterWindow materialImporter,
		EditorPreferencesWindow preferences,
		ProjectSettingsWindow projectSettings)
	{
		_workspaces = workspaces;
		_notifications = notifications;
		_windows =
		[
			new(EditorWindowIds.Scene, "Scene", scene),
			new(EditorWindowIds.Entities, "Entities", entities),
			new(EditorWindowIds.Components, "Components", components),
			new(EditorWindowIds.Assets, "Assets", assets),
			new(EditorWindowIds.AssetEditor, "Asset Editor", assetEditor),
			new(EditorWindowIds.Log, "Log", log),
			new(EditorWindowIds.Profiler, "Profiler", profiler),
			new(EditorWindowIds.MaterialImporter, "Material Importer", materialImporter),
			new(EditorWindowIds.Preferences, "Preferences", preferences),
			new(EditorWindowIds.ProjectSettings, "Project Settings", projectSettings)
		];
	}

	public IReadOnlyList<EditorWindowDescriptor> Windows => _windows;

	public bool Open(string windowId)
	{
		var descriptor = _windows.FirstOrDefault(window => window.Id == windowId);
		if (descriptor is null) return false;
		var wasOpen = _workspaces.IsWindowOpen(windowId);
		if (!descriptor.Window.CanOpen(out var reason))
		{
			if (!string.IsNullOrWhiteSpace(reason)) _notifications.ReportError(reason);
			return false;
		}
		if (!wasOpen) descriptor.Window.OnOpened();
		descriptor.Window.RequestFocus();
		_workspaces.OpenWindow(windowId);
		return true;
	}

	public void DrawVisible(EditorScene scene)
	{
		var workspace = _workspaces.ActiveWorkspace;
		foreach (var descriptor in _windows)
		{
			if (!workspace.OpenWindows.Contains(descriptor.Id)) continue;
			using (FrameProfiler.Instance.Measure(descriptor.DisplayName))
			{
				descriptor.Window.DrawInWorkspace(scene, workspace.Id, descriptor.Id, () => _workspaces.CloseWindow(descriptor.Id));
			}
		}
	}

	public EditorWindowDescriptor Get(string id) => _windows.First(window => window.Id == id);
}
