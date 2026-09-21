using ImGuiNET;

namespace WolfEngine.Editor.UI;

public static class EditorWindowIds
{
	public const string Scene = "scene";
	public const string Entities = "entities";
	public const string Components = "components";
	public const string Assets = "assets";
	public const string AssetEditor = "asset-editor";
	public const string Log = "log";
	public const string Profiler = "profiler";
	public const string MaterialImporter = "material-importer";
	public const string Preferences = "preferences";
	public const string ProjectSettings = "project-settings";

	public static readonly HashSet<string> All =
	[
		Scene, Entities, Components, Assets, AssetEditor, Log, Profiler,
		MaterialImporter, Preferences, ProjectSettings
	];
}

public sealed class EditorWorkspace
{
	internal EditorWorkspace(Guid id, string name, IEnumerable<string> openWindowIds)
	{
		Id = id;
		Name = name;
		OpenWindowIds = new HashSet<string>(openWindowIds, StringComparer.Ordinal);
	}

	public Guid Id { get; }
	public string Name { get; internal set; }
	public IReadOnlySet<string> OpenWindows => OpenWindowIds;
	internal HashSet<string> OpenWindowIds { get; }
}

public interface IEditorWorkspaceService
{
	IReadOnlyList<EditorWorkspace> Workspaces { get; }
	EditorWorkspace ActiveWorkspace { get; }
	bool TryCreate(string name, out EditorWorkspace? workspace, out string error);
	bool TryRename(Guid id, string name, out string error);
	bool Activate(Guid id);
	bool Move(Guid id, int targetIndex);
	bool Delete(Guid id);
	bool IsWindowOpen(string windowId);
	void OpenWindow(string windowId);
	void CloseWindow(string windowId);
	void LoadImGuiSettings();
	void SaveImGuiSettingsIfNeeded(bool force = false);
}

public sealed class EditorWorkspaceService : IEditorWorkspaceService, IDisposable
{
	internal static readonly Guid SceneWorkspaceId = Guid.Parse("35cc1006-e127-405e-8368-e55b9a8b2531");
	internal static readonly Guid AssetsWorkspaceId = Guid.Parse("ca584798-3557-40f4-a1ca-b6c334f4a62c");
	private const int CurrentVersion = 1;
	private static readonly TimeSpan IniSaveDelay = TimeSpan.FromMilliseconds(500);

	private readonly IEditorNotificationService _notifications;
	private readonly bool _persistToDisk;
	private readonly List<EditorWorkspace> _workspaces = new();
	private Guid _activeId;
	private DateTime _lastIniSave = DateTime.MinValue;
	private bool _imguiSettingsLoaded;

	public EditorWorkspaceService(IEditorNotificationService notifications)
	{
		_notifications = notifications;
		_persistToDisk = true;
		Restore(EditorPreferences.GetWorkspaceSettings());
	}

	internal EditorWorkspaceService(EditorWorkspacePreferences? preferences)
	{
		_notifications = new NullEditorNotificationService();
		_persistToDisk = false;
		Restore(preferences);
	}

	public IReadOnlyList<EditorWorkspace> Workspaces => _workspaces;
	public EditorWorkspace ActiveWorkspace => _workspaces.First(workspace => workspace.Id == _activeId);

	public bool TryCreate(string name, out EditorWorkspace? workspace, out string error)
	{
		if (!TryNormalizeName(name, null, out var normalized, out error))
		{
			workspace = null;
			return false;
		}

		workspace = new EditorWorkspace(Guid.NewGuid(), normalized, Array.Empty<string>());
		_workspaces.Add(workspace);
		_activeId = workspace.Id;
		Persist();
		return true;
	}

	public bool TryRename(Guid id, string name, out string error)
	{
		var workspace = _workspaces.FirstOrDefault(candidate => candidate.Id == id);
		if (workspace is null)
		{
			error = "Workspace no longer exists.";
			return false;
		}
		if (!TryNormalizeName(name, id, out var normalized, out error)) return false;
		workspace.Name = normalized;
		Persist();
		return true;
	}

	public bool Activate(Guid id)
	{
		if (_workspaces.All(workspace => workspace.Id != id)) return false;
		if (_activeId == id) return true;
		_activeId = id;
		Persist();
		return true;
	}

	public bool Move(Guid id, int targetIndex)
	{
		var currentIndex = _workspaces.FindIndex(workspace => workspace.Id == id);
		if (currentIndex < 0) return false;
		targetIndex = Math.Clamp(targetIndex, 0, _workspaces.Count - 1);
		if (currentIndex == targetIndex) return true;
		var workspace = _workspaces[currentIndex];
		_workspaces.RemoveAt(currentIndex);
		_workspaces.Insert(targetIndex, workspace);
		Persist();
		return true;
	}

	public bool Delete(Guid id)
	{
		if (_workspaces.Count <= 1) return false;
		var index = _workspaces.FindIndex(workspace => workspace.Id == id);
		if (index < 0) return false;
		_workspaces.RemoveAt(index);
		if (_activeId == id)
		{
			_activeId = _workspaces[Math.Max(0, index - 1)].Id;
		}
		Persist();
		return true;
	}

	public bool IsWindowOpen(string windowId) => ActiveWorkspace.OpenWindowIds.Contains(windowId);

	public void OpenWindow(string windowId)
	{
		if (!EditorWindowIds.All.Contains(windowId) || !ActiveWorkspace.OpenWindowIds.Add(windowId)) return;
		Persist();
	}

	public void CloseWindow(string windowId)
	{
		if (!ActiveWorkspace.OpenWindowIds.Remove(windowId)) return;
		Persist();
	}

	public void LoadImGuiSettings()
	{
		if (_imguiSettingsLoaded) return;
		_imguiSettingsLoaded = true;
		var settings = EditorPreferences.GetWorkspaceSettings()?.ImGuiSettings;
		if (!string.IsNullOrWhiteSpace(settings)) ImGui.LoadIniSettingsFromMemory(settings);
	}

	public unsafe void SaveImGuiSettingsIfNeeded(bool force = false)
	{
		if (!_imguiSettingsLoaded) return;
		var io = ImGui.GetIO();
		if (!force && (!io.WantSaveIniSettings || DateTime.UtcNow - _lastIniSave < IniSaveDelay)) return;
		EditorPreferences.SetWorkspaceImGuiSettings(ImGui.SaveIniSettingsToMemory());
		io.NativePtr->WantSaveIniSettings = 0;
		_lastIniSave = DateTime.UtcNow;
		TrySavePreferences();
	}

	public void Dispose() => SaveImGuiSettingsIfNeeded(true);

	private void Restore(EditorWorkspacePreferences? preferences)
	{
		if (preferences?.Version == CurrentVersion)
		{
			foreach (var saved in preferences.Workspaces)
			{
				if (saved.Id == Guid.Empty || string.IsNullOrWhiteSpace(saved.Name) ||
				    _workspaces.Any(workspace => workspace.Id == saved.Id || string.Equals(workspace.Name, saved.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
					continue;
				_workspaces.Add(new EditorWorkspace(saved.Id, saved.Name.Trim()[..Math.Min(saved.Name.Trim().Length, 64)],
					saved.OpenWindowIds.Where(EditorWindowIds.All.Contains)));
			}
		}

		if (_workspaces.Count == 0)
		{
			_workspaces.Add(new EditorWorkspace(SceneWorkspaceId, "Scene",
				[EditorWindowIds.Entities, EditorWindowIds.Scene, EditorWindowIds.Components, EditorWindowIds.AssetEditor, EditorWindowIds.Log]));
			_workspaces.Add(new EditorWorkspace(AssetsWorkspaceId, "Assets",
				[EditorWindowIds.Assets, EditorWindowIds.Components, EditorWindowIds.AssetEditor, EditorWindowIds.Log]));
		}
		_activeId = preferences?.ActiveWorkspaceId is { } active && _workspaces.Any(workspace => workspace.Id == active)
			? active
			: _workspaces[0].Id;
		Persist(saveToDisk: false);
	}

	private bool TryNormalizeName(string name, Guid? exceptId, out string normalized, out string error)
	{
		normalized = name.Trim();
		if (normalized.Length == 0) { error = "Enter a workspace name."; return false; }
		if (normalized.Length > 64) { error = "Workspace names can contain at most 64 characters."; return false; }
		var candidateName = normalized;
		if (_workspaces.Any(workspace => workspace.Id != exceptId && string.Equals(workspace.Name, candidateName, StringComparison.OrdinalIgnoreCase)))
		{
			error = "A workspace with that name already exists.";
			return false;
		}
		error = string.Empty;
		return true;
	}

	private void Persist(bool saveToDisk = true)
	{
		EditorPreferences.SetWorkspaceSettings(new EditorWorkspacePreferences
		{
			Version = CurrentVersion,
			ActiveWorkspaceId = _activeId,
			ImGuiSettings = EditorPreferences.GetWorkspaceSettings()?.ImGuiSettings ?? string.Empty,
			Workspaces = _workspaces.Select(workspace => new EditorWorkspacePreference
			{
				Id = workspace.Id,
				Name = workspace.Name,
				OpenWindowIds = workspace.OpenWindowIds.OrderBy(id => id, StringComparer.Ordinal).ToList()
			}).ToList()
		});
		if (saveToDisk) TrySavePreferences();
	}

	private void TrySavePreferences()
	{
		if (!_persistToDisk) return;
		try { EditorPreferences.Save(); }
		catch (Exception exception) { _notifications.ReportError($"Could not save editor workspaces: {exception.Message}"); }
	}

	private sealed class NullEditorNotificationService : IEditorNotificationService
	{
		public void ReportInfo(string message) { }
		public void ReportError(string message) { }
		public bool TryDequeue(out EditorNotification notification) { notification = default; return false; }
	}
}
