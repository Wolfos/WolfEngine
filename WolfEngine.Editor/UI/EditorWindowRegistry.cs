using ImGuiNET;
using WolfEngine.Profiling;

namespace WolfEngine.Editor.UI;

public sealed record EditorWindowDescriptor(string Id, string DisplayName, EditorWindow Window);

/// <summary>
/// What one registered editor window looked like on the frame it was last submitted. Automation reads
/// these to assert on panel visibility, docking and tab selection without screen-scraping a capture.
/// </summary>
public sealed record EditorWindowUiState(
	string Id,
	string DisplayName,
	bool IsOpen,
	bool IsDocked,
	uint DockId,
	bool IsSelectedTab,
	bool IsFocused,
	bool IsHovered,
	float X,
	float Y,
	float Width,
	float Height);

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
		var restoring = _tabRestoreFramesLeft > 0 && selection is not null;
		if (restoring) RequestSelectedTabs(workspace, selection!);

		foreach (var descriptor in _windows)
		{
			if (!workspace.OpenWindows.Contains(descriptor.Id))
			{
				descriptor.Window.OnHidden();
				continue;
			}
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

	/// <summary>
	/// Snapshots every registered window as of the last drawn frame. Windows that the active workspace
	/// does not have open report <see cref="EditorWindowUiState.IsOpen"/> false and zeroed geometry,
	/// because they were never submitted to Dear ImGui.
	/// </summary>
	public IReadOnlyList<EditorWindowUiState> GetUiState()
	{
		var workspace = _workspaces.ActiveWorkspace;
		var states = new List<EditorWindowUiState>(_windows.Length);
		foreach (var descriptor in _windows)
		{
			var window = descriptor.Window;
			var isOpen = workspace.OpenWindows.Contains(descriptor.Id);
			states.Add(new EditorWindowUiState(
				descriptor.Id,
				descriptor.DisplayName,
				isOpen,
				isOpen && window.DockId != 0,
				isOpen ? window.DockId : 0,
				isOpen && window.IsSelectedTab,
				isOpen && window.IsFocused,
				isOpen && window.IsHovered,
				isOpen ? window.Position.X : 0,
				isOpen ? window.Position.Y : 0,
				isOpen ? window.Size.X : 0,
				isOpen ? window.Size.Y : 0));
		}
		return states;
	}

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

		selection.SelectedTabWindowIds.Clear();
		foreach (var descriptor in _windows)
		{
			if (!workspace.OpenWindows.Contains(descriptor.Id)) continue;
			if (descriptor.Window.IsSelectedTab) selection.SelectedTabWindowIds.Add(descriptor.Id);
			if (descriptor.Window.IsFocused) selection.FocusedWindowId = descriptor.Id;
		}
	}

	private sealed class WorkspaceTabSelection
	{
		public HashSet<string> SelectedTabWindowIds { get; } = new(StringComparer.Ordinal);
		public string? FocusedWindowId { get; set; }
	}
}
