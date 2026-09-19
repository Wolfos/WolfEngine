using ImGuiNET;
using WolfEngine.Profiling;

namespace WolfEngine.Editor.UI;

public sealed record EditorWindowDescriptor(string Id, string DisplayName, EditorWindow Window);

public sealed class EditorWindowRegistry
{
	private const int TabRestoreFrames = 3;

	private readonly IEditorWorkspaceService _workspaces;
	private readonly IEditorNotificationService _notifications;
	private readonly EditorWindowDescriptor[] _windows;
	private readonly Dictionary<Guid, WorkspaceTabSelection> _tabSelections = new();
	private Guid _lastDrawnWorkspaceId;
	private int _tabRestoreFramesLeft;

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
		if (workspace.Id != _lastDrawnWorkspaceId)
		{
			_lastDrawnWorkspaceId = workspace.Id;
			_tabRestoreFramesLeft = _tabSelections.ContainsKey(workspace.Id) ? TabRestoreFrames : 0;
		}

		_tabSelections.TryGetValue(workspace.Id, out var selection);
		var restoring = _tabRestoreFramesLeft > 0 && selection is not null &&
			Environment.GetEnvironmentVariable("WOLF_TAB_RESTORE_DISABLED") != "1"; // TEMP-DIAG
		if (restoring) RequestSelectedTabs(workspace, selection!);

		foreach (var descriptor in _windows)
		{
			if (!workspace.OpenWindows.Contains(descriptor.Id)) continue;
			using (FrameProfiler.Instance.Measure(descriptor.DisplayName))
			{
				descriptor.Window.DrawInWorkspace(scene, workspace.Id, descriptor.Id, () => _workspaces.CloseWindow(descriptor.Id));
			}
		}

		if (restoring)
		{
			_tabRestoreFramesLeft--;
			RestoreFocusedWindow(workspace, selection!);
			return;
		}
		RecordSelectedTabs(workspace);
	}

	public EditorWindowDescriptor Get(string id) => _windows.First(window => window.Id == id);

	private void RequestSelectedTabs(EditorWorkspace workspace, WorkspaceTabSelection selection)
	{
		foreach (var windowId in selection.SelectedTabWindowIds)
		{
			if (workspace.OpenWindows.Contains(windowId)) Get(windowId).Window.RequestFocus();
		}
	}

	private void RestoreFocusedWindow(EditorWorkspace workspace, WorkspaceTabSelection selection)
	{
		// Every restored tab asked for focus, so the last window drawn would otherwise keep it.
		if (selection.FocusedWindowId is not { } windowId || !workspace.OpenWindows.Contains(windowId)) return;
		ImGui.SetWindowFocus(Get(windowId).Window.GetWorkspaceImGuiName(workspace.Id, windowId));
	}

	private void RecordSelectedTabs(EditorWorkspace workspace)
	{
		if (!_tabSelections.TryGetValue(workspace.Id, out var selection))
		{
			selection = new WorkspaceTabSelection();
			_tabSelections[workspace.Id] = selection;
		}

		var previous = string.Join(",", selection.SelectedTabWindowIds.OrderBy(id => id, StringComparer.Ordinal)); // TEMP-DIAG
		selection.SelectedTabWindowIds.Clear();
		foreach (var descriptor in _windows)
		{
			if (!workspace.OpenWindows.Contains(descriptor.Id)) continue;
			if (descriptor.Window.IsSelectedTab) selection.SelectedTabWindowIds.Add(descriptor.Id);
			if (descriptor.Window.IsFocused) selection.FocusedWindowId = descriptor.Id;
		}
		var current = string.Join(",", selection.SelectedTabWindowIds.OrderBy(id => id, StringComparer.Ordinal)); // TEMP-DIAG
		if (current != previous) Console.Error.WriteLine($"TEMP-DIAG selected tabs [{workspace.Name}] {current} focused={selection.FocusedWindowId}"); // TEMP-DIAG
	}

	private sealed class WorkspaceTabSelection
	{
		public HashSet<string> SelectedTabWindowIds { get; } = new(StringComparer.Ordinal);
		public string? FocusedWindowId { get; set; }
	}
}
