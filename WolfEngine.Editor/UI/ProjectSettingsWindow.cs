using System.Text.Json;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

public sealed class ProjectSettingsWindow : EditorWindow
{
	private readonly IEditorProjectService _projectService;
	private readonly IEditorNotificationService _notificationService;
	private WolfEngineBuildConfig? _config;
	private string? _loadedProjectPath;

	public ProjectSettingsWindow(IEditorProjectService projectService, IEditorNotificationService notificationService)
	{
		_projectService = projectService;
		_notificationService = notificationService;
	}

	public override string Name => "Project Settings";

	public override bool CanOpen(out string? reason)
	{
		if (_projectService.HasOpenProject == false)
		{
			reason = "Open a project before editing project settings.";
			return false;
		}
		reason = null;
		return true;
	}

	public override void OnOpened() => Load();

	public override void Draw(EditorScene scene)
	{
		if (_projectService.HasOpenProject == false)
		{
			CloseCurrentWorkspaceWindow();
			return;
		}
		if (_config is null || !string.Equals(_loadedProjectPath, _projectService.ProjectRootPath, StringComparison.Ordinal))
			Load();
		if (_config is null)
			return;

		ImGui.SetNextWindowSize(new System.Numerics.Vector2(560, 360), ImGuiCond.FirstUseEver);
		Begin();
		ImGui.TextUnformatted("Project Scenes");
		ImGui.TextDisabled("The first scene is launched when the game starts. All listed scenes are included in builds.");
		ImGui.Separator();

		var scenes = GetScenes();
		for (var index = 0; index < _config.SceneIds.Count; index++)
		{
			var sceneId = _config.SceneIds[index];
			var projectScene = scenes.FirstOrDefault(asset => asset.Id == sceneId);
			var label = projectScene is null ? $"Missing scene ({sceneId:D})" : projectScene.Name;
			ImGui.TextUnformatted(index == 0 ? $"Start: {label}" : label);
			ImGui.SameLine();
			if (index > 0 && ImGui.SmallButton($"Make Start##{sceneId:D}"))
			{
				_config.SceneIds.RemoveAt(index);
				_config.SceneIds.Insert(0, sceneId);
			}
			ImGui.SameLine();
			if (ImGui.SmallButton($"Remove##{sceneId:D}"))
			{
				_config.SceneIds.RemoveAt(index);
				index--;
			}
		}

		ImGui.Spacing();
		if (ImGui.BeginCombo("Add Scene", "Select a scene..."))
		{
			foreach (var projectScene in scenes.Where(candidate => !_config.SceneIds.Contains(candidate.Id)))
			{
				if (ImGui.Selectable($"{projectScene.Name}##{projectScene.Id:D}"))
					_config.SceneIds.Add(projectScene.Id);
			}
			ImGui.EndCombo();
		}

		ImGui.Spacing();
		if (ImGui.Button("Save"))
		{
			Save();
		}

		ImGui.SameLine();
		if (ImGui.Button("Reload"))
		{
			Load();
		}

		ImGui.End();
	}

	private IReadOnlyList<AssetDatabaseEntry> GetScenes() => _projectService.CurrentAssetDatabase.Assets
		.Where(asset => asset.Type == AssetType.Scene)
		.OrderBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
		.ToArray();

	private void Load()
	{
		try
		{
			var projectPath = _projectService.ProjectRootPath ?? throw new InvalidOperationException("No project is open.");
			var configPath = Path.Combine(projectPath, "WolfEngineBuild.json");
			_config = File.Exists(configPath)
				? JsonSerializer.Deserialize<WolfEngineBuildConfig>(File.ReadAllBytes(configPath), AssetJson.SerializerOptions)
				: new WolfEngineBuildConfig();
			if (_config is null)
				throw new InvalidDataException("WolfEngineBuild.json is invalid.");
			_config.SetSceneIds(_config.GetSceneIds());
			_loadedProjectPath = projectPath;
		}
		catch (Exception exception)
		{
			_config = null;
			_notificationService.ReportError($"Could not load project settings: {exception.Message}");
		}
	}

	private void Save()
	{
		try
		{
			if (_config!.SceneIds.Count == 0)
				throw new InvalidOperationException("Add at least one project scene.");
			_config.SetSceneIds(_config.SceneIds);
			var configPath = Path.Combine(_projectService.ProjectRootPath!, "WolfEngineBuild.json");
			File.WriteAllBytes(configPath, JsonSerializer.SerializeToUtf8Bytes(_config, AssetJson.SerializerOptions));
			_notificationService.ReportInfo("Project settings saved.");
		}
		catch (Exception exception)
		{
			_notificationService.ReportError($"Could not save project settings: {exception.Message}");
		}
	}
}
